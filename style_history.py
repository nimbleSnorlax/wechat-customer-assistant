from __future__ import annotations

import base64
import json
import os
import sqlite3
import sys
import re
import unicodedata
from collections import Counter
from contextlib import closing, contextmanager, redirect_stdout
from datetime import datetime, timedelta
from pathlib import Path
from tempfile import TemporaryDirectory
from uuid import uuid4

from wechat_cli.core.context import AppContext
from wechat_cli.core.messages import (
    MSG_TYPE_FILTERS,
    _build_message_filters,
    _is_safe_msg_table_name,
    _iter_table_contexts,
    _load_name2id_maps,
    _load_search_contexts_from_db,
    decompress_content,
    resolve_chat_context,
)

PER_CHAT = 30
HISTORY_PAGE_ROWS = 1000
MIN_REPLY_CONTEXT = 5
REPLY_CONTEXT_LIMIT = 50
DEFAULT_REPLY_CONTEXT = 20
WECHAT_ALIAS = re.compile(r"[A-Za-z0-9_-]{1,60}")
READER_WINDOWS_MODULES = ""
CONTACT_LABEL_FIELD = 30
SYSTEM_CONTACTS = {"filehelper", "weixin", "fmessage", "medianote", "newsapp", "brandsessionholder",
                   "notifymessage", "weixinreminder", "appbrandcustomerservicemsg"}


def read_varint(data, offset):
    value = 0
    for shift in range(0, 70, 7):
        if offset >= len(data):
            break
        byte = data[offset]
        offset += 1
        value |= (byte & 0x7f) << shift
        if not byte & 0x80:
            return value, offset
    raise ValueError("Incomplete contact data")


def contact_label_ids(data):
    """Read the comma-separated label IDs from a WeChat contact's extension data."""
    if not isinstance(data, bytes):
        return None
    offset = 0
    labels = None
    while offset < len(data):
        field, offset = read_varint(data, offset)
        wire_type = field & 7
        if field < 8 or (field >> 3 == CONTACT_LABEL_FIELD and wire_type != 2):
            raise ValueError("Unsupported contact label layout")
        if wire_type == 0:
            _, offset = read_varint(data, offset)
            continue
        if wire_type == 2:
            size, offset = read_varint(data, offset)
        elif wire_type in (1, 5):
            size = 8 if wire_type == 1 else 4
        else:
            raise ValueError("Unsupported contact data")
        end = offset + size
        if end > len(data):
            raise ValueError("Incomplete contact data")
        if field >> 3 == CONTACT_LABEL_FIELD:
            value = data[offset:end]
            if not re.fullmatch(rb"[0-9,]*", value):
                raise ValueError("Unsupported contact label data")
            if labels is None:
                labels = set()
            labels.update(int(part) for part in value.split(b",") if part)
        offset = end
    return labels


class ContactDirectoryError(ValueError):
    pass


def local_wechat_directories():
    import winreg

    roots = {Path.home(), Path.home() / "Documents"}
    try:
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, r"Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders") as key:
            documents = winreg.QueryValueEx(key, "Personal")[0]
        documents = Path(os.path.expandvars(documents))
        if documents.is_absolute():
            roots.add(documents)
    except OSError:
        pass
    appdata = os.environ.get("APPDATA")
    if appdata:
        configs = Path(appdata) / "Tencent" / "xwechat" / "config"
        for ini in configs.glob("*.ini"):
            try:
                with ini.open("rb") as config_file:
                    raw = config_file.read(4097)
                if len(raw) > 4096:
                    continue
                try:
                    encoding = "utf-16" if raw.startswith((b"\xff\xfe", b"\xfe\xff")) else "utf-8-sig"
                    value = raw.decode(encoding).strip()
                except UnicodeDecodeError:
                    value = raw.decode("gbk").strip()
                if value and not any(character in value for character in "\r\n\x00"):
                    folder = Path(os.path.expandvars(value))
                    if folder.is_absolute():
                        roots.add(folder)
            except (OSError, UnicodeError):
                pass
    directories = set()
    for root in roots:
        account_root = root if root.name.casefold() == "xwechat_files" else root / "xwechat_files"
        for candidate in account_root.glob("*/db_storage"):
            if (candidate / "contact" / "contact.db").is_file():
                directories.add(candidate.resolve())
    return sorted(directories), any((root / "WeChat Files").is_dir() for root in roots)


def initialize_reader(config_path, process_id, alias=""):
    if os.name != "nt" or type(process_id) is not int or process_id <= 0:
        raise ContactDirectoryError("请先登录一个受支持的电脑版微信。")
    from wechat_cli.keys import extract_keys

    config_path = Path(config_path)
    candidates, has_legacy_storage = local_wechat_directories()
    if not candidates:
        if has_legacy_storage:
            raise ContactDirectoryError("只找到旧版微信历史目录。请打开电脑版微信 4.x，导入旧记录后再识别。")
        raise ContactDirectoryError("微信聊天资料还在加载。加载完成后，请重新识别微信。")
    config_path.parent.mkdir(parents=True, exist_ok=True)
    with TemporaryDirectory(dir=config_path.parent) as scratch:
        matched = None
        for index, directory in enumerate(candidates):
            candidate_dir = Path(scratch) / str(index)
            candidate_dir.mkdir()
            keys = candidate_dir / "all_keys.json"
            candidate = candidate_dir / "config.json"
            try:
                with open(os.devnull, "w", encoding="utf-8") as discard, redirect_stdout(discard):
                    extract_keys(str(directory), str(keys), pid=process_id)
                    candidate.write_text(json.dumps({"db_dir": str(directory)}), encoding="utf-8")
                    app = AppContext(str(candidate))
                    try:
                        source = read_source_identity(app)
                    finally:
                        app.cache.cleanup()
            except (RuntimeError, OSError, ValueError, TypeError, sqlite3.Error):
                continue
            if not source["historyAvailable"] or alias and alias.casefold() not in {
                    source["alias"].casefold(), source["profile"].casefold()}:
                continue
            if matched is not None:
                raise ContactDirectoryError("当前进程匹配到多个账号资料。请重新登录需要使用的微信账号。")
            matched = source, keys, candidate
        if matched:
            source, keys, candidate = matched
            saved_keys = config_path.with_name(f"wechat-reader-{uuid4().hex}.keys.json")
            candidate.write_text(json.dumps({"db_dir": source["dbDir"], "keys_file": str(saved_keys)}),
                                 encoding="utf-8")
            keys.rename(saved_keys)
            try:
                candidate.replace(config_path)
            except OSError:
                saved_keys.unlink(missing_ok=True)
                raise
            return {"state": "ready", "profile": source["profile"]}
    raise ContactDirectoryError("暂时无法读取当前账号的资料。请保持微信登录并重新识别。")


@contextmanager
def contact_snapshot(app):
    path = app.cache.get("contact/contact.db")
    if not path:
        path = Path(app.decrypted_dir) / "contact" / "contact.db"
        if not path.is_file():
            raise ContactDirectoryError("暂时无法读取本机微信通讯录。")
    snapshot = Path(path).resolve().as_uri() + "?mode=ro"
    with closing(sqlite3.connect(snapshot, uri=True, timeout=3)) as connection:
        yield connection


def source_identity(app, connection):
    directory = Path(app.db_dir)
    if directory.name.casefold() != "db_storage":
        raise ContactDirectoryError("请选择对应微信账号的本机 db_storage 资料。")
    folder = directory.parent.name
    stem = re.sub(r"_(?:[a-z0-9]{4}|[0-9a-f]{5,})$", "", folder, flags=re.I)
    rows = connection.execute(
        "SELECT username, alias, nick_name FROM contact WHERE username COLLATE NOCASE IN (?, ?)", (folder, stem)
    ).fetchall()
    if len(rows) != 1 or not rows[0][0]:
        raise ContactDirectoryError("无法核对本机资料所属的微信账号。")
    profile, alias, name = rows[0]
    return {"profile": profile, "alias": alias or profile, "name": name or profile,
            "historyAvailable": bool(app.msg_db_keys), "dbDir": str(directory.resolve()),
            "keysFile": app.keys_file}


def read_source_identity(app, expected_owner=None):
    with contact_snapshot(app) as connection:
        source = source_identity(app, connection)
    if expected_owner is not None and source["profile"] != expected_owner:
        raise ContactDirectoryError("本机只读资料与当前微信账号不一致。")
    return source


@contextmanager
def contact_directory(config_path, profile):
    app = AppContext(config_path)
    with contact_snapshot(app) as connection:
        source = source_identity(app, connection)
        if not profile or source["profile"] != profile:
            raise ContactDirectoryError("本机只读资料与当前微信账号不一致。")
        if not WECHAT_ALIAS.fullmatch(source["alias"]):
            raise ContactDirectoryError("无法核对本机资料所属的微信账号。")
        yield connection, source["alias"]


def directory_contacts(connection, profile, owner):
    rows = connection.execute(
        "SELECT username, alias, remark, nick_name, extra_buffer FROM contact "
        "WHERE local_type=1 AND delete_flag=0 AND username IS NOT NULL"
    )
    contacts = []
    for username, alias, remark, nickname, extra in rows:
        if (not username or username == profile or username.startswith("gh_") or username.endswith("@chatroom")
                or username in SYSTEM_CONTACTS):
            continue
        alias = alias or username
        name = (remark or nickname or username).strip()
        if not WECHAT_ALIAS.fullmatch(alias) or alias.casefold() == owner.casefold() or not 0 < len(name) <= 80:
            continue
        contacts.append((name, alias, nickname or "", username, extra))
    return contacts


def unique_contact_aliases(contacts):
    names = Counter(name for name, *_ in contacts)
    aliases = Counter(alias.casefold() for _, alias, *_ in contacts)
    return {alias for name, alias, *_ in contacts if names[name] == 1 and aliases[alias.casefold()] == 1}


def contact_search_text(text):
    return "".join(unicodedata.normalize("NFKC", text).casefold().split())


def search_contact_directory(config_path, profile, query, *, exact=False):
    needle = contact_search_text(query)
    if not needle:
        return {"people": []}
    matches = []
    try:
        with contact_directory(config_path, profile) as (connection, owner):
            contacts = directory_contacts(connection, profile, owner)
            unique_aliases = unique_contact_aliases(contacts)
            if exact:
                named = [(name, alias) for name, alias, nickname, _, _ in contacts
                         if needle in {contact_search_text(name), contact_search_text(nickname)}]
                if len(named) != 1 or named[0][1] not in unique_aliases:
                    return {"people": []}
                name, alias = named[0]
                return {"people": [{"id": f"uia:{owner}:{alias}", "name": name}]}
            for name, alias, nickname, username, _ in contacts:
                if alias not in unique_aliases:
                    continue
                tokens = [contact_search_text(text) for text in (name, nickname, alias, username)]
                if needle in tokens:
                    rank = 0
                elif any(term.startswith(needle) for term in tokens):
                    rank = 1
                elif any(needle in term for term in tokens):
                    rank = 2
                else:
                    continue
                matches.append((rank, name, alias))
    except ContactDirectoryError as error:
        return {"error": str(error)}
    matches.sort(key=lambda contact: (contact[0], contact[1].casefold()))
    return {"people": [{"id": f"uia:{owner}:{alias}", "name": name} for _, name, alias in matches[:20]]}


def read_contact_tag_catalog(config_path, profile):
    try:
        with contact_directory(config_path, profile) as (connection, owner):
            labels = {label_id: name.strip() for label_id, name in connection.execute(
                "SELECT label_id_, label_name_ FROM contact_label ORDER BY sort_order_, label_id_"
            ) if type(label_id) is int and label_id > 0 and isinstance(name, str) and name.strip()}
            if not labels:
                return {"owner": owner, "labels": [], "people": [], "includesUntagged": False}
            contacts = directory_contacts(connection, profile, owner)
    except ContactDirectoryError as error:
        return {"error": str(error)}

    people = []
    needs_review = False
    for name, alias, _, _, data in contacts:
        try:
            identifiers = contact_label_ids(data)
        except ValueError:
            identifiers = None
        if identifiers is None:
            needs_review = True
            continue
        tags = [name for label_id, name in labels.items() if label_id in identifiers]
        people.append({"id": f"uia:{owner}:{alias}", "name": name, "tags": tags})
    if not people and needs_review:
        return {"error": "当前微信的标签成员资料暂时无法核对。请先在微信里打开通讯录后刷新。"}
    unique_ids = {f"uia:{owner}:{alias}" for alias in unique_contact_aliases(contacts)}
    return {"owner": owner, "labels": list(labels.values()), "people": [person for person in people if person["id"] in unique_ids],
            "includesUntagged": not needs_review}


def mask_history_text(text):
    for pattern, replacement in (
        (r"https?://[^\s，。；]+", "[链接]"),
        (r"[\w.+-]+@[\w.-]+\.[A-Za-z]{2,}", "[邮箱]"),
        (r"(?:\+?86[- ]?)?(?<!\d)1[3-9]\d{9}(?!\d)", "[手机号]"),
        (r"(?<!\d)\d{6,}[\dXx]*(?!\d)", "[编号]"),
    ):
        text = re.sub(pattern, replacement, text, flags=re.IGNORECASE)
    return text.strip()


def own_replies(config_path, chats, days, expected_owner=None):
    app = AppContext(config_path)
    owner = read_source_identity(app, expected_owner)["profile"]

    start_ts = int((datetime.now() - timedelta(days=days)).timestamp())
    filters, parameters = _build_message_filters(start_ts=start_ts, msg_type_filter=MSG_TYPE_FILTERS["text"])
    partial = False
    chat_samples = []
    for chat in chats:
        ctx = resolve_chat_context(chat, app.msg_db_keys, app.cache, app.decrypted_dir)
        if not ctx or ctx["username"] != chat or not ctx["db_path"]:
            partial = True
            continue
        entries = set()
        for table in _iter_table_contexts(ctx):
            name = table["table_name"]
            if not _is_safe_msg_table_name(name):
                partial = True
                continue
            try:
                snapshot = Path(table["db_path"]).resolve().as_uri() + "?mode=ro"
                with closing(sqlite3.connect(snapshot, uri=True, timeout=3)) as connection:
                    sender_ids = _load_name2id_maps(connection)
                    own_ids = [identifier for identifier, username in sender_ids.items() if username == owner]
                    if not own_ids:
                        partial = partial or not sender_ids
                        continue
                    placeholders = ",".join("?" for _ in own_ids)
                    where = " AND ".join([*filters, f"real_sender_id IN ({placeholders})"])
                    rows = connection.execute(
                        f"SELECT create_time, message_content, WCDB_CT_message_content FROM [{name}] "
                        f"WHERE {where} ORDER BY create_time DESC, local_id DESC LIMIT ?",
                        (*parameters, *own_ids, PER_CHAT * 2),
                    )
                    for stamp, content, content_type in rows:
                        text = decompress_content(content, content_type)
                        if not isinstance(text, str):
                            continue
                        if ctx["is_group"]:
                            text = text.removeprefix(owner + ":\n")
                        text = text.strip()
                        if text and "\ufffd" not in text:
                            entries.add((stamp, text[:1200]))
            except (OSError, sqlite3.Error, ValueError):
                partial = True
        if entries:
            chat_samples.append([text for _, text in sorted(entries, reverse=True)[:PER_CHAT]])
    return {"chatSamples": chat_samples, "partial": partial}


def readable_text(content, content_type):
    text = decompress_content(content, content_type)
    return text.strip() if isinstance(text, str) and "\ufffd" not in text else ""


def recent_reply_turns(connection, table, senders, owner, chat, limit):
    roles = {owner: "self", chat: "customer"}
    allowed = {sender_id: roles[username] for sender_id, username in senders.items() if username in roles}
    if not allowed:
        return []
    placeholders = ",".join("?" for _ in allowed)
    rows = connection.execute(
        f"SELECT create_time, local_id, real_sender_id, message_content, WCDB_CT_message_content FROM [{table}] "
        f"WHERE local_type=1 AND real_sender_id IN ({placeholders}) "
        "ORDER BY create_time DESC, local_id DESC LIMIT ?",
        (*allowed, limit + 1),
    )
    turns = []
    for stamp, local_id, sender_id, content, content_type in rows:
        text = mask_history_text(readable_text(content, content_type))[:500]
        if text:
            turns.append((stamp, local_id, allowed[sender_id], text))
    return turns


def latest_incoming_text(config_path, chats, expected_owner, context_chats=(), context_limit=DEFAULT_REPLY_CONTEXT):
    app = AppContext(config_path)
    owner = read_source_identity(app, expected_owner)["profile"]
    result = {}
    context_chats = set(context_chats)
    if type(context_limit) is not int or not MIN_REPLY_CONTEXT <= context_limit <= REPLY_CONTEXT_LIMIT:
        context_limit = DEFAULT_REPLY_CONTEXT
    for chat in chats:
        if chat == owner:
            continue
        context = resolve_chat_context(chat, app.msg_db_keys, app.cache, app.decrypted_dir)
        if not context or context["username"] != chat or context["is_group"] or not context["db_path"]:
            continue
        latest = None
        turns = set()
        with_context = chat in context_chats
        try:
            for table in _iter_table_contexts(context):
                name = table["table_name"]
                if not _is_safe_msg_table_name(name):
                    raise ValueError("Invalid message table.")
                snapshot = Path(table["db_path"]).resolve().as_uri() + "?mode=ro"
                with closing(sqlite3.connect(snapshot, uri=True, timeout=3)) as connection:
                    row = connection.execute(
                        f"SELECT create_time, local_id, local_type, real_sender_id, "
                        f"message_content, WCDB_CT_message_content FROM [{name}] "
                        "ORDER BY create_time DESC, local_id DESC LIMIT 1"
                    ).fetchone()
                    if row is None:
                        continue
                    most_recent = latest is None or row[:2] >= latest[0]
                    if not most_recent and not with_context:
                        continue
                    senders = _load_name2id_maps(connection)
                    if most_recent:
                        text = readable_text(row[4], row[5]) if row[2] == 1 and senders.get(row[3]) == chat else ""
                        if latest and row[:2] == latest[0] and text != latest[1]:
                            text = ""
                        latest = (row[:2], text)
                    if with_context:
                        turns.update(recent_reply_turns(connection, name, senders, owner, chat, context_limit))
        except (OSError, sqlite3.Error, ValueError):
            continue
        if latest:
            result[chat] = {"timestamp": latest[0][0], "text": latest[1]}
            if with_context and latest[1]:
                current_text = mask_history_text(latest[1])[:500]
                earlier = sorted(turn for turn in turns if turn[:2] < latest[0]
                                 and (turn[0], turn[2], turn[3]) != (latest[0][0], "customer", current_text))
                result[chat]["context"] = [
                    {"role": role, "text": text, "daysBefore": (latest[0][0] - stamp) // 86400}
                    for stamp, _, role, text in earlier[-context_limit:]
                ]
    return {"messages": result}


def history_page(config_path, selection):
    app = AppContext(config_path)
    owner = read_source_identity(app, selection["owner"])["profile"]
    source_order = sorted(app.msg_db_keys)
    cursor = selection.get("cursor") or {}
    index = cursor.get("source", 0)
    if index >= len(source_order):
        return {"cursor": None, "samples": [], "read": 0, "failed": 0}
    next_source = {"source": index + 1}
    samples = []
    read, failed = 0, 0
    try:
        path = app.cache.get(source_order[index])
        if not path:
            raise OSError("History source unavailable")
        snapshot = Path(path).resolve().as_uri() + "?mode=ro"
        with closing(sqlite3.connect(snapshot, uri=True, timeout=3)) as connection:
            senders = _load_name2id_maps(connection)
            if owner not in senders.values():
                raise ValueError("Cannot identify own messages")
            tables = sorted(_load_search_contexts_from_db(connection, path, {}), key=lambda item: item["table_name"])
            for chat in tables:
                table = chat["table_name"]
                if not _is_safe_msg_table_name(table) or table < cursor.get("table", ""):
                    continue
                resume = table == cursor.get("table")
                preceding = cursor.get("preceding", "") if resume else ""
                after = cursor.get("after", 0) if resume else 0
                try:
                    rows = connection.execute(
                        f"SELECT local_id, real_sender_id, message_content, WCDB_CT_message_content FROM [{table}] "
                        "WHERE local_id > ? AND create_time <= ? AND local_type = 1 ORDER BY local_id LIMIT ?",
                        (after, selection["asOf"], HISTORY_PAGE_ROWS - read),
                    ).fetchall()
                    for after, sender_id, content, content_type in rows:
                        sender = senders.get(sender_id)
                        text = decompress_content(content, content_type) if sender else ""
                        if not isinstance(text, str) or "\ufffd" in text:
                            continue
                        text = mask_history_text(text.removeprefix(sender + ":\n") if sender else text)
                        if text and sender == owner:
                            samples.append([preceding, text[:480]])
                        elif text:
                            preceding = text[:220]
                    read += len(rows)
                    if read >= HISTORY_PAGE_ROWS:
                        return {"cursor": {"source": index, "table": table, "after": after, "preceding": preceding},
                                "samples": samples, "read": read, "failed": failed}
                except (OSError, sqlite3.Error, ValueError):
                    failed += 1
    except (OSError, sqlite3.Error, ValueError):
        failed += 1
    return {"cursor": next_source, "samples": samples, "read": read, "failed": failed}


if __name__ == "__main__":
    try:
        selection = json.loads(sys.stdin.read().lstrip("\ufeff"))
        if selection.get("mode") == "initialize":
            if READER_WINDOWS_MODULES:
                import wechat_cli
                with TemporaryDirectory() as runtime:
                    bundle = Path(runtime) / "reader.zip"
                    bundle.write_bytes(base64.b64decode(READER_WINDOWS_MODULES))
                    wechat_cli.__path__.insert(0, str(bundle) + "/wechat_cli")
                    print(json.dumps(initialize_reader(sys.argv[1], selection["processId"], selection["alias"])))
            else:
                print(json.dumps(initialize_reader(sys.argv[1], selection["processId"], selection["alias"])))
            sys.exit(0)
        config = json.loads(Path(sys.argv[1]).read_text(encoding="utf-8"))
        if not isinstance(config, dict) or not isinstance(config.get("db_dir"), str) or not config["db_dir"].strip():
            raise ContactDirectoryError("请先配置当前微信账号的本机只读资料。")
        if selection.get("mode") == "source":
            result = read_source_identity(AppContext(sys.argv[1]))
        elif selection.get("mode") == "background":
            result = latest_incoming_text(sys.argv[1], selection["chats"], selection["owner"],
                                          selection.get("contextChats", []), selection.get("contextLimit", DEFAULT_REPLY_CONTEXT))
        elif selection.get("mode") == "learning":
            result = history_page(sys.argv[1], selection)
        elif selection.get("mode") == "tags":
            result = read_contact_tag_catalog(sys.argv[1], selection["owner"])
        elif selection.get("mode") == "contacts":
            result = search_contact_directory(sys.argv[1], selection["owner"], selection["query"],
                                              exact=selection.get("exact") is True)
        else:
            result = own_replies(sys.argv[1], selection["chats"], selection["days"], selection.get("owner"))
    except ContactDirectoryError as error:
        result = {"error": str(error)}
    except Exception:
        result = {"error": "本机历史资料暂不可读，请检查微信查询工具和账号配置。"}
    print(json.dumps(result, ensure_ascii=False))


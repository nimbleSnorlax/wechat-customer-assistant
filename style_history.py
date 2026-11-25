from __future__ import annotations

import json
import sqlite3
import sys
import re
from contextlib import closing
from datetime import datetime, timedelta
from pathlib import Path

from wechat_cli.core.contacts import get_self_username
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


def mask_history_text(text):
    for pattern, replacement in (
        (r"https?://[^\s，。；]+", "[链接]"),
        (r"[\w.+-]+@[\w.-]+\.[A-Za-z]{2,}", "[邮箱]"),
        (r"(?:\+?86[- ]?)?(?<!\d)1[3-9]\d{9}(?!\d)", "[手机号]"),
        (r"(?<!\d)\d{6,}[\dXx]*(?!\d)", "[编号]"),
    ):
        text = re.sub(pattern, replacement, text, flags=re.IGNORECASE)
    return text.strip()


def own_replies(config_path, chats, days):
    app = AppContext(config_path)
    owner = get_self_username(app.db_dir, app.cache, app.decrypted_dir)
    if not owner:
        return {"error": "暂时无法确认资料所属的微信账号，请检查本机历史查询设置。"}

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
    owner = get_self_username(app.db_dir, app.cache, app.decrypted_dir)
    if not owner or owner != expected_owner:
        return {"error": "后台接收无法确认本机微信账号。"}
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
    owner = get_self_username(app.db_dir, app.cache, app.decrypted_dir)
    if not owner or owner != selection["owner"]:
        return {"error": "无法确认当前历史资料所属的微信账号。"}
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
        if selection.get("mode") == "background":
            result = latest_incoming_text(sys.argv[1], selection["chats"], selection["owner"],
                                          selection.get("contextChats", []), selection.get("contextLimit", DEFAULT_REPLY_CONTEXT))
        elif selection.get("mode") == "learning":
            result = history_page(sys.argv[1], selection)
        else:
            result = own_replies(sys.argv[1], selection["chats"], selection["days"])
    except Exception:
        result = {"error": "本机历史资料暂不可读，请检查微信查询工具和账号配置。"}
    print(json.dumps(result, ensure_ascii=False))


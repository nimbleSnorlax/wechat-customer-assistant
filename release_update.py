from __future__ import annotations

import hashlib
import http.client
import io
import json
import os
import re
import sys
import tempfile
import urllib.error
import urllib.parse
import urllib.request
import zipfile
from pathlib import Path

EXIT_TO_UPDATE = 75
FILES = ("server.py", "style_history.py", "index.html", "release_update.py", "app_version.txt")
VERSION_PATTERN = re.compile(r"v?((?:0|[1-9][0-9]{0,5})\.(?:0|[1-9][0-9]{0,5})\.(?:0|[1-9][0-9]{0,5}))")
REPOSITORY_PATTERN = re.compile(r"[A-Za-z0-9][A-Za-z0-9-]{0,38}/[A-Za-z0-9][A-Za-z0-9_.-]{0,99}")
FOLDER_PATTERN = re.compile(r"[0-9.]+-[0-9a-f]{12}-[a-z0-9_]{6,16}")
MAX_DOWNLOAD = 8 * 1024 * 1024
MAX_UNPACKED = 4 * 1024 * 1024
API_HOST = "https://api.github.com"
ASSET_HOSTS = {"release-assets.githubusercontent.com", "objects.githubusercontent.com", "github-releases.githubusercontent.com"}


class UpdateError(Exception):
    pass


def version(value):
    match = VERSION_PATTERN.fullmatch(value.strip()) if isinstance(value, str) else None
    if not match:
        raise UpdateError("版本号请使用 v0.1.0 这样的格式。")
    return match[1]


def version_key(value):
    return tuple(map(int, version(value).split(".")))


def package_name(value):
    return f"WeChatAssistant-{version(value)}-update.zip"


def read_json(path):
    try:
        value = json.loads(path.read_text(encoding="utf-8-sig"))
        return value if isinstance(value, dict) else {}
    except (OSError, ValueError):
        return {}


def write_json(path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    handle, temporary_path = tempfile.mkstemp(prefix=path.name + ".", dir=path.parent)
    try:
        with os.fdopen(handle, "w", encoding="utf-8") as file:
            json.dump(value, file, ensure_ascii=False)
            file.flush()
            os.fsync(file.fileno())
        os.replace(temporary_path, path)
    finally:
        Path(temporary_path).unlink(missing_ok=True)


def update_dir(home):
    return home / ".run" / "update"


def installed_version(root):
    return version((root / "app_version.txt").read_text(encoding="utf-8-sig"))


def configured_repository(home):
    name = os.environ.get("WECHAT_UPDATE_GITHUB_REPO") or read_json(home / "update-source.json").get("repository", "")
    if name and (not isinstance(name, str) or not REPOSITORY_PATTERN.fullmatch(name)):
        raise UpdateError("GitHub 发布仓库请填写所有者/仓库名。")
    return name


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, request, fp, code, msg, headers, newurl):
        return None


class DownloadRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, request, fp, code, msg, headers, newurl):
        target = urllib.parse.urlsplit(newurl)
        if (target.scheme != "https" or target.netloc.lower() not in ASSET_HOSTS
                or target.username or target.password):
            raise UpdateError("GitHub 下载地址异常，已取消更新。")
        redirected = super().redirect_request(request, fp, code, msg, headers, newurl)
        if redirected:
            redirected.remove_header("Authorization")
        return redirected


def read_download(response, size_limit, on_download):
    data = bytearray()
    while len(data) <= size_limit:
        chunk = response.read(min(16 * 1024, size_limit + 1 - len(data)))
        if not chunk:
            return bytes(data)
        data.extend(chunk)
        if len(data) <= size_limit:
            on_download(len(data), size_limit)
    raise UpdateError("GitHub 返回的文件大于允许的更新包大小。")


def github_request(path, accept, size_limit, download=False, *, on_download=None):
    headers = {"Accept": accept, "User-Agent": "WeChatAssistant-Update"}
    if token := os.environ.get("WECHAT_UPDATE_GITHUB_TOKEN"):
        headers["Authorization"] = "Bearer " + token
    request = urllib.request.Request(API_HOST + path, headers=headers)
    try:
        redirect = DownloadRedirect() if download else NoRedirect()
        with urllib.request.build_opener(redirect).open(request, timeout=15) as response:
            if on_download is not None:
                return read_download(response, size_limit, on_download)
            data = response.read(size_limit + 1)
            if len(data) > size_limit:
                raise UpdateError("GitHub 返回的文件大于允许的更新包大小。")
            return data
    except urllib.error.HTTPError as error:
        if error.code == 404:
            raise UpdateError("GitHub 上还没有可访问的正式发行版，请检查仓库或下载授权。") from error
        raise UpdateError(f"GitHub 暂时无法完成更新。HTTP {error.code}。") from error
    except (OSError, http.client.HTTPException) as error:
        raise UpdateError("暂时连不上 GitHub，可以稍后重试。") from error


def find_update(repository, current):
    raw = github_request(f"/repos/{repository}/releases/latest", "application/vnd.github+json", 1_000_000)
    try:
        release = json.loads(raw)
        newer = version(release["tag_name"])
        if release["draft"] or release["prerelease"] or version_key(newer) <= version_key(current):
            return None
        asset = next((item for item in release["assets"] if item["name"] == package_name(newer)), None)
        if not asset:
            raise UpdateError(f"新版 v{newer} 还没有上传工作台更新包。")
        size = asset["size"]
        digest = re.fullmatch(r"sha256:([0-9a-f]{64})", asset.get("digest") or "", re.IGNORECASE)
        if (not digest or type(size) is not int or not 0 < size <= MAX_DOWNLOAD
                or type(asset["id"]) is not int or asset["id"] <= 0):
            raise UpdateError("这个发行版的更新包缺少可用的完整性信息。")
        return {"version": newer, "notes": (release.get("body") or "")[:2200],
                "asset": asset["id"], "size": size, "sha256": digest[1].lower()}
    except (ValueError, KeyError, TypeError) as error:
        raise UpdateError("GitHub 的发行版信息格式有误。") from error


def unpack_release(raw, expected_version):
    try:
        with zipfile.ZipFile(io.BytesIO(raw)) as archive:
            entries = archive.infolist()
            names = [entry.filename for entry in entries]
            if len(names) != len(FILES) or set(names) != set(FILES):
                raise UpdateError("更新包的文件清单与工作台不匹配。")
            if sum(entry.file_size for entry in entries) > MAX_UNPACKED:
                raise UpdateError("更新包解包后的文件过大。")
            files = {name: archive.read(name) for name in FILES}
        if version(files["app_version.txt"].decode("utf-8-sig")) != expected_version:
            raise UpdateError("更新包内的版本和发行版标签不一致。")
        for name, data in files.items():
            if name.endswith(".py"):
                compile(data.decode("utf-8-sig"), name, "exec")
        if not files["index.html"].startswith(b"<!doctype html>"):
            raise UpdateError("更新包内的配置页面无效。")
        return files
    except (zipfile.BadZipFile, OSError, ValueError, RuntimeError, SyntaxError) as error:
        raise UpdateError("更新包解包或语法检查失败。") from error


def stage_update(home, repository, release, *, progress=None):
    path = f"/repos/{repository}/releases/assets/{release['asset']}"
    on_download = None
    if progress is not None:
        progress("downloading", 0, release["size"])
        on_download = lambda received, total: progress("downloading", received, total)
    raw = github_request(path, "application/octet-stream", release["size"], download=True, on_download=on_download)
    if progress is not None:
        progress("verifying")
    if len(raw) != release["size"] or hashlib.sha256(raw).hexdigest() != release["sha256"]:
        raise UpdateError("下载文件与 GitHub 提供的校验信息不一致。")
    files = unpack_release(raw, release["version"])
    if progress is not None:
        progress("preparing")
    base = update_dir(home)
    releases = base / "releases"
    releases.mkdir(parents=True, exist_ok=True)
    # A candidate becomes visible to the launcher only after all files are complete.
    with tempfile.TemporaryDirectory(prefix="stage-", dir=releases) as staged:
        candidate_name = f"{release['version']}-{release['sha256'][:12]}-{Path(staged).name.removeprefix('stage-')}"
        if not FOLDER_PATTERN.fullmatch(candidate_name):
            raise UpdateError("无法生成有效的更新目录。")
        folder = Path(staged) / candidate_name
        folder.mkdir()
        for name, data in files.items():
            (folder / name).write_bytes(data)
        (folder / "app_version.txt").write_text(release["version"] + "\n", encoding="utf-8")
        os.replace(folder, releases / candidate_name)
    write_json(base / "result.json", {})
    write_json(base / "pending.json", {"folder": candidate_name, "version": release["version"]})


def resolve_release(home, record):
    name = record.get("folder")
    if not isinstance(name, str) or not FOLDER_PATTERN.fullmatch(name):
        return None
    base = (update_dir(home) / "releases").resolve()
    candidate = (base / name).resolve()
    if candidate.parent != base or not all((candidate / file).is_file() for file in FILES):
        return None
    try:
        if installed_version(candidate) == record.get("version"):
            return candidate
    except (OSError, UpdateError):
        return None
    return None


def build_package(root, value):
    target = version(value)
    output = root / "dist" / package_name(target)
    output.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(dir=output.parent) as staged:
        temporary = Path(staged) / output.name
        with zipfile.ZipFile(temporary, "w", compression=zipfile.ZIP_DEFLATED) as archive:
            for name in FILES:
                if name == "app_version.txt":
                    archive.writestr(name, target + "\n")
                else:
                    archive.write(root / name, name)
        unpack_release(temporary.read_bytes(), target)
        os.replace(temporary, output)
    return output


if __name__ == "__main__":
    if len(sys.argv) != 3 or sys.argv[1] != "--build":
        raise SystemExit("用法：python release_update.py --build 0.1.1")
    print(build_package(Path(__file__).resolve().parent, sys.argv[2]))


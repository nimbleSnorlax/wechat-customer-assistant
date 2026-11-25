"""Build a per-user Windows x64 installer with its own runtime and release source."""
import argparse
import hashlib
import io
import json
import os
import subprocess
import sys
import tempfile
import urllib.request
from pathlib import Path
from zipfile import ZIP_DEFLATED, ZipFile

ROOT = Path(__file__).resolve().parent.parent
HERE = Path(__file__).resolve().parent
APP_ICON = HERE / "WeChatAssistant.ico"
sys.path.insert(0, str(ROOT))
from release_update import FILES, REPOSITORY_PATTERN, installed_version, version

PYTHON_PACKAGE = "python-3.13.17-embed-amd64.zip"
PYTHON_URL = "https://www.python.org/ftp/python/3.13.17/" + PYTHON_PACKAGE
PYTHON_SHA256 = "9e35319bffedab8b36ad44d8f377cb4f1733bcec257a0b6d04d94f01486ddb38"
WEBVIEW_VERSION = "1.0.3800.47"
WEBVIEW_SHA256 = "56c9f26bdd07916a2d1949fb58a5c7e434dfa1173577dca879206050c4e718db"
WEBVIEW_FILES = ("lib/net462/Microsoft.Web.WebView2.Core.dll", "lib/net462/Microsoft.Web.WebView2.WinForms.dll",
                 "runtimes/win-x64/native/WebView2Loader.dll", "LICENSE.txt")
READER_MODULES = (
    "__init__.py", "commands/__init__.py", "commands/contacts.py", "commands/sessions.py",
    "core/__init__.py", "core/config.py", "core/contacts.py", "core/context.py",
    "core/crypto.py", "core/db_cache.py", "core/key_utils.py", "core/messages.py",
    "output/__init__.py", "output/formatter.py",
)
READER_PACKAGES = ("click==8.5.0", "colorama==0.4.6", "pycryptodome==3.24.1", "zstandard==0.25.0")


def python_runtime():
    cache = ROOT / ".run" / PYTHON_PACKAGE
    if cache.is_file():
        data = cache.read_bytes()
    else:
        with urllib.request.urlopen(PYTHON_URL, timeout=45) as response:
            data = response.read(24 * 1024 * 1024)
    if hashlib.sha256(data).hexdigest() != PYTHON_SHA256:
        raise SystemExit("Python 运行包校验失败，请重新下载。")
    if not cache.is_file():
        cache.write_bytes(data)
    return ZipFile(io.BytesIO(data))


def webview_components(staging):
    cache = ROOT / ".run" / f"webview2-{WEBVIEW_VERSION}.nupkg"
    if cache.is_file():
        data = cache.read_bytes()
    else:
        package = f"microsoft.web.webview2.{WEBVIEW_VERSION}.nupkg"
        url = f"https://api.nuget.org/v3-flatcontainer/microsoft.web.webview2/{WEBVIEW_VERSION}/{package}"
        with urllib.request.urlopen(url, timeout=45) as response:
            data = response.read(16 * 1024 * 1024)
    if hashlib.sha256(data).hexdigest() != WEBVIEW_SHA256:
        raise SystemExit("桌面显示组件校验失败。")
    if not cache.is_file():
        cache.write_bytes(data)
    components = []
    with ZipFile(io.BytesIO(data)) as archive:
        for entry in WEBVIEW_FILES:
            target = staging / Path(entry).name
            target.write_bytes(archive.read(entry))
            components.append(target)
    return components


def add_tree(archive, directory):
    for path in sorted(directory.rglob("*")):
        if path.is_file():
            archive.write(path, path.relative_to(ROOT).as_posix())


def compile_cs(source, output, *arguments):
    csc = Path(os.environ["WINDIR"]) / "Microsoft.NET/Framework64/v4.0.30319/csc.exe"
    subprocess.run([str(csc), "/nologo", "/codepage:65001", "/target:winexe",
                    "/r:System.Windows.Forms.dll", f"/win32icon:{APP_ICON}", f"/out:{output}",
                    *arguments, str(HERE / source)], check=True)


def add_reader(archive, source, dependencies):
    prefix = "runtime/python/thirdparty/"
    archive.write(HERE / "wechat-reader.py", "runtime/wechat-reader.py")
    for module in READER_MODULES:
        archive.write(source / "wechat_cli" / module, prefix + "wechat_cli/" + module)
    archive.write(source / "LICENSE", "licenses/wechat-cli.txt")
    if (source / "NOTICE").is_file():
        archive.write(source / "NOTICE", "licenses/wechat-cli-NOTICE.txt")
    for path in sorted(dependencies.rglob("*")):
        if path.is_file():
            archive.write(path, prefix + path.relative_to(dependencies).as_posix())


def build(repository, output, release_version=None, reader_source=None):
    if repository and not REPOSITORY_PATTERN.fullmatch(repository):
        raise SystemExit("发布仓库格式应为 GitHub 所有者/仓库名。")
    reader_source = reader_source or Path(os.environ.get(
        "WECHAT_ASSISTANT_READER_SOURCE", Path.home() / "wxtools" / "wechat-cli-plus"))
    if not (reader_source / "LICENSE").is_file() or not all(
            (reader_source / "wechat_cli" / module).is_file() for module in READER_MODULES):
        raise SystemExit("找不到只读组件源码，请用 --reader-source 指定 wechat-cli-plus 目录。")
    bridge = ROOT / ".run" / "sdk"
    dotnet = ROOT / ".dotnet"
    if not (bridge / "WeChatBridge.dll").is_file() or not (dotnet / "dotnet.exe").is_file():
        raise SystemExit("请先准备 Windows x64 的 .NET 运行环境并编译微信 SDK。")
    app_version = version(release_version) if release_version is not None else installed_version(ROOT)
    output = (output or ROOT / "dist" / f"WeChatAssistant-{app_version}-Setup-x64.exe").resolve()
    output.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="wechat-setup-") as temp:
        staging = Path(temp)
        starter = staging / "WeChatAssistant.exe"
        components = webview_components(staging)
        compile_cs("Start.cs", starter, "/platform:x64", "/r:System.Drawing.dll", "/r:System.Net.Http.dll",
                   *(f"/r:{path}" for path in components[:2]))
        dependencies = staging / "reader-dependencies"
        subprocess.run(
            [sys.executable, "-m", "pip", "install", "--target", str(dependencies), "--ignore-installed",
             "--only-binary=:all:", "--platform", "win_amd64", "--python-version", "3.13",
             "--implementation", "cp", "--abi", "cp313", "--no-compile", "--disable-pip-version-check",
             "--index-url", "https://pypi.org/simple", *READER_PACKAGES],
            check=True,
        )
        payload = staging / "payload.zip"
        with ZipFile(payload, "w", ZIP_DEFLATED, compresslevel=4) as archive:
            for name in (*FILES, "launcher.py"):
                if name == "app_version.txt":
                    archive.writestr(name, app_version + "\n")
                else:
                    archive.write(ROOT / name, name)
            archive.writestr("update-source.json", json.dumps({"repository": repository} if repository else {}))
            archive.write(starter, starter.name)
            archive.write(APP_ICON, APP_ICON.name)
            for component in components[:3]:
                archive.write(component, component.name)
            archive.write(components[3], "licenses/Microsoft.Web.WebView2.txt")
            archive.write(ROOT / "WeChatAuto.SDK/LICENSE", "licenses/WeChatAuto.SDK.txt")
            with python_runtime() as runtime:
                for entry in runtime.infolist():
                    if not entry.is_dir() and not entry.filename.endswith("._pth"):
                        archive.writestr("runtime/python/" + entry.filename, runtime.read(entry))
            archive.writestr("runtime/python/python313._pth", "python313.zip\n.\nthirdparty\n")
            add_reader(archive, reader_source, dependencies)
            for library in (ROOT / ".python").glob("pypdf*"):
                for path in library.rglob("*"):
                    if path.is_file() and path.suffix != ".pyc":
                        archive.write(path, "runtime/python/thirdparty/" + path.relative_to(ROOT / ".python").as_posix())
            for name in ("dotnet.exe", "LICENSE.txt", "ThirdPartyNotices.txt"):
                archive.write(dotnet / name, ".dotnet/" + name)
            for name in ("host/fxr", "shared/Microsoft.NETCore.App", "shared/Microsoft.WindowsDesktop.App"):
                add_tree(archive, dotnet / name)
            for path in sorted(bridge.iterdir()):
                if path.suffix in {".dll", ".exe"} or path.name in {
                    "WeChatBridge.runtimeconfig.json", "WeChatBridge.deps.json"
                }:
                    archive.write(path, path.relative_to(ROOT).as_posix())
            for name in ("x64", "runtimes/win", "runtimes/win-x64"):
                add_tree(archive, bridge / name)
        compile_cs("Setup.cs", output, "/r:Microsoft.CSharp.dll", "/r:System.IO.Compression.dll",
                   "/r:System.IO.Compression.FileSystem.dll", f"/resource:{payload},WorkbenchPayload")
    print(output)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("repository", nargs="?")
    parser.add_argument("--offline", action="store_true", help="生成未配置 GitHub 更新源的安装包")
    parser.add_argument("--output", type=Path)
    parser.add_argument("--version")
    parser.add_argument("--reader-source", type=Path)
    args = parser.parse_args()
    if bool(args.repository) == args.offline:
        parser.error("请指定 GitHub 发布仓库，或使用 --offline。")
    build(args.repository, args.output, args.version, args.reader_source)

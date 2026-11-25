"""Build a Windows installer for the local machine's existing Python and SDK."""
import argparse
import io
import json
import os
import subprocess
import sys
import tempfile
from pathlib import Path
from zipfile import ZIP_DEFLATED, ZipFile

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT))
from release_update import FILES, installed_version, package_name, version, version_key


def build(output, requested_version):
    base = installed_version(ROOT)
    newer = version(requested_version)
    if version_key(newer) <= version_key(base):
        raise SystemExit("测试更新版本号需要大于当前版本。")
    runtime = {
        "sdk": str(ROOT / ".run" / "sdk" / "WeChatBridge.dll"),
        "dotnet": str(ROOT / ".dotnet" / "dotnet.exe"),
        "updateVersion": newer,
    }
    for key in ("sdk", "dotnet"):
        if not Path(runtime[key]).is_file():
            raise SystemExit("请先生成本机 SDK：" + runtime[key])
    files = {name: (ROOT / name).read_bytes() for name in FILES}
    host = b'API_HOST = "https://api.github.com"'
    if host not in files["release_update.py"]:
        raise SystemExit("本机测试版无法定位更新源。")
    files["release_update.py"] = files["release_update.py"].replace(
        host, b'API_HOST = "http://127.0.0.1:" + str(int(os.environ["WECHAT_TEST_FEED_PORT"]))', 1)
    marker = b'</head>'
    if marker not in files["index.html"]:
        raise SystemExit("本机测试版无法添加页面版本标记。")
    base_html = files["index.html"]
    update = io.BytesIO()
    with ZipFile(update, "w", ZIP_DEFLATED) as archive:
        for name, contents in files.items():
            if name == "app_version.txt":
                contents = (newer + "\n").encode("ascii")
            if name == "index.html":
                meta = f'<meta name="wechat-test-release" content="{newer}">\n'.encode("ascii")
                contents = base_html.replace(marker, meta + marker, 1)
            archive.writestr(name, contents)

    python = Path(sys.executable)
    command = ('@echo off\r\nsetlocal\r\nchcp 65001 >nul\r\ncd /d "%~dp0"\r\n'
               f'set "PYTHONPATH={ROOT / ".python"}"\r\nset "PYTHONUTF8=1"\r\n'
               f'"{python}" -u test_start.py --open\r\nif errorlevel 1 pause\r\n')
    framework = Path(os.environ["WINDIR"]) / "Microsoft.NET" / "Framework64" / "v4.0.30319"
    output = output.resolve()
    output.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="wechat-test-build-") as work:
        payload = Path(work) / "payload.zip"
        with ZipFile(payload, "w", ZIP_DEFLATED) as archive:
            for name, contents in files.items():
                archive.writestr(name, contents)
            archive.write(ROOT / "launcher.py", "launcher.py")
            archive.write(Path(__file__).with_name("test_start.py"), "test_start.py")
            archive.writestr("test-runtime.json", json.dumps(runtime))
            archive.writestr("Start.cmd", command.encode("utf-8"))
            archive.writestr(package_name(newer), update.getvalue())

        subprocess.run([str(framework / "csc.exe"), "/nologo", "/codepage:65001", "/target:winexe",
                        "/r:System.Windows.Forms.dll", "/r:System.IO.Compression.dll",
                        "/r:System.IO.Compression.FileSystem.dll",
                        f"/resource:{payload},WorkbenchPayload", f"/out:{output}",
                        str(Path(__file__).with_name("Setup.cs"))], check=True)
    print(output)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("output", type=Path)
    parser.add_argument("updated_version")
    args = parser.parse_args()
    build(args.output, args.updated_version)

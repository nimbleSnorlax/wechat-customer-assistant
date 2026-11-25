"""Start the installed local test workbench and its loopback release feed."""
import hashlib
import json
import os
import socket
import sys
import urllib.request
import webbrowser
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from threading import Thread


class LocalFeedServer(ThreadingHTTPServer):
    allow_reuse_address = False

    def server_bind(self):
        self.socket.setsockopt(socket.SOL_SOCKET, socket.SO_EXCLUSIVEADDRUSE, 1)
        super().server_bind()


def main():
    home = Path(__file__).resolve().parent
    runtime = json.loads((home / "test-runtime.json").read_text(encoding="utf-8"))
    port = int((home / "port.txt").read_text(encoding="ascii"))
    os.environ.update(
        WECHAT_ASSISTANT_PORT=str(port), WECHAT_TEST_FEED_PORT=str(port + 1),
        WECHAT_ASSISTANT_HOME=str(home), WECHAT_ASSISTANT_DATA=str(home / "data"),
        WECHAT_ASSISTANT_SDK=runtime["sdk"], WECHAT_ASSISTANT_DOTNET=runtime["dotnet"],
        WECHAT_UPDATE_GITHUB_REPO="offline/wechat-assistant",
    )
    os.environ.pop("WECHAT_UPDATE_GITHUB_TOKEN", None)
    for key in ("sdk", "dotnet"):
        if not Path(runtime[key]).is_file():
            raise SystemExit("本机测试运行组件找不到：" + runtime[key])
    import launcher
    from release_update import package_name

    version = runtime["updateVersion"]
    name = package_name(version)
    instance = hashlib.sha256(str(home).casefold().encode()).hexdigest().encode("ascii")
    update = (home / name).read_bytes()
    manifest = json.dumps({
        "tag_name": "v" + version, "draft": False, "prerelease": False,
        "body": "本机升级测试发行版。安装后保留自动回复设置与运行记录。",
        "assets": [{"name": name, "id": 1, "size": len(update),
                    "digest": "sha256:" + hashlib.sha256(update).hexdigest()}],
    }, ensure_ascii=False).encode("utf-8")

    class ReleaseFeed(BaseHTTPRequestHandler):
        def do_GET(self):
            if self.path == "/repos/offline/wechat-assistant/releases/latest":
                mime, body = "application/json; charset=utf-8", manifest
            elif self.path == "/repos/offline/wechat-assistant/releases/assets/1":
                mime, body = "application/octet-stream", update
            elif self.path == "/local-instance":
                mime, body = "text/plain", instance
            else:
                self.send_error(404)
                return
            self.send_response(200)
            self.send_header("Content-Type", mime)
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)

        def log_message(self, *_):
            pass

    try:
        feed = LocalFeedServer(("127.0.0.1", port + 1), ReleaseFeed)
    except OSError:
        url = f"http://127.0.0.1:{port + 1}/local-instance"
        try:
            with urllib.request.urlopen(url, timeout=1) as response:
                existing = response.read(65) == instance
        except OSError:
            existing = False
        if not existing:
            raise SystemExit("本机测试更新端口已被占用。")
        if "--open" in sys.argv:
            webbrowser.open(f"http://127.0.0.1:{port}/")
        return 0
    with feed:
        Thread(target=feed.serve_forever, daemon=True).start()
        try:
            return launcher.main()
        finally:
            feed.shutdown()


if __name__ == "__main__":
    raise SystemExit(main())

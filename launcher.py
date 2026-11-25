from __future__ import annotations

import msvcrt
import os
import subprocess
import sys
import time
import uuid
from pathlib import Path

HOME_DIR = Path(__file__).resolve().parent
sys.path.insert(0, str(HOME_DIR))
from release_update import EXIT_TO_UPDATE, installed_version, read_json, resolve_release, update_dir, version_key, write_json

STATE = update_dir(HOME_DIR)


def launch(root, open_browser=False):
    marker = STATE / ("ready-" + uuid.uuid4().hex)
    env = os.environ.copy()
    env.update(WECHAT_ASSISTANT_HOME=str(HOME_DIR), WECHAT_ASSISTANT_READY_FILE=str(marker),
               WECHAT_ASSISTANT_MANAGED_UPDATE="1")
    command = [sys.executable, "-u", str(root / "server.py")]
    if open_browser:
        command.append("--open")
    process = subprocess.Popen(command, env=env, cwd=HOME_DIR)
    try:
        deadline = time.monotonic() + 15
        while process.poll() is None and time.monotonic() < deadline:
            if marker.is_file():
                return process
            time.sleep(0.1)
        if process.poll() is None:
            process.terminate()
            process.wait(timeout=5)
        return None
    finally:
        marker.unlink(missing_ok=True)


def run():
    STATE.mkdir(parents=True, exist_ok=True)
    active_file = STATE / "active.json"
    pending_file = STATE / "pending.json"
    previous = resolve_release(HOME_DIR, read_json(active_file)) or HOME_DIR
    queued = read_json(pending_file)
    open_browser = "--open" in sys.argv
    while True:
        candidate = resolve_release(HOME_DIR, queued)
        process = None
        if candidate and version_key(installed_version(candidate)) > version_key(installed_version(previous)):
            pending_file.unlink(missing_ok=True)
            process = launch(candidate)
            if process:
                write_json(active_file, queued)
                write_json(STATE / "result.json", {"state": "installed", "version": queued["version"]})
                previous = candidate
            else:
                write_json(STATE / "result.json", {"state": "failed", "version": queued["version"]})
        elif queued:
            pending_file.unlink(missing_ok=True)
            write_json(STATE / "result.json", {"state": "failed", "version": queued.get("version", "")})
        if not process:
            process = launch(previous, open_browser)
        elif open_browser:
            import webbrowser
            port = os.environ.get("WECHAT_ASSISTANT_PORT", "8766")
            webbrowser.open(f"http://127.0.0.1:{port}/", new=2)
        if not process:
            return 1
        open_browser = False
        try:
            code = process.wait()
        except KeyboardInterrupt:
            if process.poll() is None:
                process.terminate()
            return 0
        if code != EXIT_TO_UPDATE:
            return code
        queued = read_json(pending_file)


def main():
    STATE.mkdir(parents=True, exist_ok=True)
    with (STATE / "launcher.lock").open("a+b") as lock:
        lock.seek(0)
        try:
            msvcrt.locking(lock.fileno(), msvcrt.LK_NBLCK, 1)
        except OSError:
            if "--open" in sys.argv:
                import webbrowser
                port = int(os.environ.get("WECHAT_ASSISTANT_PORT", "8766"))
                webbrowser.open(f"http://127.0.0.1:{port}/", new=2)
            print("工作台已经启动。")
            return 0
        return run()


if __name__ == "__main__":
    raise SystemExit(main())


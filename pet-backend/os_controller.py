from __future__ import annotations

import os
import subprocess
import webbrowser
from pathlib import Path
from typing import Any
from urllib.parse import urlsplit
from config import settings_manager
from schemas import ActionCommand


class OSController:
    async def execute(self, command: ActionCommand) -> dict[str, Any]:
        if command.type == "noop":
            return {"ok": True, "executed": False, "message": "没有需要执行的电脑操作。"}
        if command.type == "open_url":
            return self._open_url(command.target)
        if command.type == "open_app":
            return self._open_app(command.target)
        return {"ok": False, "executed": False, "message": "该指令需要通过屏幕分析或语音服务执行。"}

    @staticmethod
    def _dry_run(message: str) -> dict[str, Any]:
        return {"ok": True, "executed": False, "dry_run": True, "message": "电脑操作未启用，仅预览：" + message}

    def _open_url(self, target: str | None) -> dict[str, Any]:
        target = (target or "").strip()
        try:
            parsed = urlsplit(target)
            valid = parsed.scheme in {"http", "https"} and parsed.hostname and not parsed.username and not parsed.password
        except ValueError:
            valid = False
        if not valid or any(ord(char) < 32 for char in target):
            return {"ok": False, "executed": False, "message": "只允许打开有效的 http(s) 网页地址。"}
        if not settings_manager.get().enable_real_rpa:
            return self._dry_run("打开网页 " + target)
        try:
            if not webbrowser.open(target, new=2):
                return {"ok": False, "executed": False, "message": "系统未能打开默认浏览器。"}
        except Exception:
            return {"ok": False, "executed": False, "message": "打开浏览器失败，请检查默认浏览器设置。"}
        return {"ok": True, "executed": True, "message": "已请求浏览器打开网页。"}

    @staticmethod
    def _app_path(target: str) -> Path | None:
        if os.name != "nt":
            return None
        windows = Path(os.environ.get("SystemRoot", r"C:\Windows"))
        if target in {"notepad", "calc"}:
            return windows / "System32" / (target + ".exe")
        if target == "explorer":
            return windows / "explorer.exe"
        if target == "msedge":
            for variable in ["ProgramFiles(x86)", "ProgramFiles", "LOCALAPPDATA"]:
                base = os.environ.get(variable)
                if base:
                    candidate = Path(base) / "Microsoft" / "Edge" / "Application" / "msedge.exe"
                    if candidate.is_file():
                        return candidate
        return None

    def _open_app(self, target: str | None) -> dict[str, Any]:
        target = (target or "").strip().lower()
        value = settings_manager.get()
        if target not in {"notepad", "calc", "explorer", "msedge"} or target not in value.allowed_apps:
            return {"ok": False, "executed": False, "message": "该应用不在允许的启动名单中。"}
        if not value.enable_real_rpa:
            return self._dry_run("启动 " + target)
        path = self._app_path(target)
        if path is None or not path.is_file():
            return {"ok": False, "executed": False, "message": "未找到该应用；此功能支持 Windows 桌面。"}
        try:
            subprocess.Popen([str(path)], shell=False)
        except OSError:
            return {"ok": False, "executed": False, "message": "系统拒绝启动该应用。"}
        return {"ok": True, "executed": True, "message": "已启动 " + target + "。"}

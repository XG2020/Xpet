from __future__ import annotations

import base64
from pathlib import Path
from errors import ServiceError

MAX_FILE_BYTES = 8 * 1024 * 1024
TEXT_EXTENSIONS = {".txt", ".md", ".csv", ".json", ".log", ".py", ".cs", ".js", ".ts", ".html", ".css", ".xml", ".yaml", ".yml", ".toml", ".ini", ".shader"}
IMAGE_EXTENSIONS = {".png": "image/png", ".jpg": "image/jpeg", ".jpeg": "image/jpeg", ".webp": "image/webp"}


def capture_screen() -> str:
    try:
        import mss
        import mss.tools
    except ImportError:
        raise ServiceError("缺少 mss 截屏依赖，请运行后端安装脚本。", 503) from None
    try:
        with mss.mss() as screen:
            shot = screen.grab(screen.monitors[1])
            png = mss.tools.to_png(shot.rgb, shot.size)
        if len(png) > 16 * 1024 * 1024:
            raise ServiceError("屏幕截图过大，请改用较小的图片文件。", 413)
        return "data:image/png;base64," + base64.b64encode(png).decode("ascii")
    except ServiceError:
        raise
    except Exception:
        raise ServiceError("无法截取屏幕，请确认当前桌面会话可用。", 503) from None


def read_selected_file(raw_path: str) -> tuple[str | None, str | None]:
    path = Path(raw_path)
    # Only local, explicitly selected files. No UNC shares, devices or URLs.
    if not path.is_absolute() or raw_path.startswith(("\\\\", "//")):
        raise ServiceError("请选择本机文件的绝对路径。", 422)
    extension = path.suffix.lower()
    if extension not in TEXT_EXTENSIONS and extension not in IMAGE_EXTENSIONS:
        raise ServiceError("支持文本、代码、JSON、CSV，以及 PNG/JPG/WebP 图片。", 415)
    try:
        if not path.is_file():
            raise ServiceError("所选文件不存在或不是普通文件。", 404)
        if path.stat().st_size > MAX_FILE_BYTES:
            raise ServiceError("文件不能超过 8 MB。", 413)
        with path.open("rb") as file:
            data = file.read(MAX_FILE_BYTES + 1)
        if len(data) > MAX_FILE_BYTES:
            raise ServiceError("文件不能超过 8 MB。", 413)
    except ServiceError:
        raise
    except OSError:
        raise ServiceError("无法读取所选文件，请检查访问权限。", 403) from None
    if extension in IMAGE_EXTENSIONS:
        valid = ((extension == ".png" and data.startswith(b"\x89PNG\r\n\x1a\n"))
                 or (extension in {".jpg", ".jpeg"} and data.startswith(b"\xff\xd8\xff"))
                 or (extension == ".webp" and data[:4] == b"RIFF" and data[8:12] == b"WEBP"))
        if not valid:
            raise ServiceError("图片内容与扩展名不匹配。", 422)
        return "data:" + IMAGE_EXTENSIONS[extension] + ";base64," + base64.b64encode(data).decode("ascii"), None
    if b"\x00" in data and not data.startswith((b"\xff\xfe", b"\xfe\xff")):
        raise ServiceError("该文件不是可识别的文本文件。", 415)
    encoding = "utf-16" if data.startswith((b"\xff\xfe", b"\xfe\xff")) else "utf-8-sig"
    try:
        text = data.decode(encoding)
    except UnicodeDecodeError:
        try:
            text = data.decode("gb18030")
        except UnicodeDecodeError:
            raise ServiceError("无法解码文本，请另存为 UTF-8 后重试。", 422) from None
    if len(text) > 30000:
        text = text[:30000] + "\n[文件较长，以下内容已截断]"
    return None, f"文件名：{path.name}\n{text}"

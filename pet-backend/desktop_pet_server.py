"""Standalone distribution entry point; PyInstaller bundles the Python runtime."""
from __future__ import annotations

import argparse
import json
import os
import re
import secrets
import sys
from pathlib import Path
from fastapi import HTTPException, Request


def configure_launcher_shutdown(application, server, token: str) -> None:
    """The launching process owns this nonce. Never expose shutdown to unauthenticated callers."""
    def verify(request: Request) -> None:
        if not request.client or request.client.host not in {"127.0.0.1", "::1"}:
            raise HTTPException(403, "Local launcher access required.")
        candidate = request.headers.get("x-xpet-shutdown-token", "")
        if not candidate or not secrets.compare_digest(candidate, token):
            raise HTTPException(403, "Launcher authorization required.")

    @application.get("/internal/launcher/status", include_in_schema=False)
    async def launcher_status(request: Request):
        verify(request)
        return {"ok": True, "pid": os.getpid()}

    @application.post("/internal/launcher/shutdown", include_in_schema=False)
    async def launcher_shutdown(request: Request):
        verify(request)
        server.should_exit = True
        return {"ok": True}


def configure_windowless_logging(data_dir: Path | None) -> None:
    if sys.stdout is not None and sys.stderr is not None:
        return
    directory = Path(os.environ.get("XPET_LOG_DIR", "")) if os.environ.get("XPET_LOG_DIR") else (
        data_dir / "logs" if data_dir else Path(os.environ.get("LOCALAPPDATA", str(Path.home()))) / "Xpet" / "logs")
    directory.mkdir(parents=True, exist_ok=True)
    path = directory / "backend.log"
    if path.exists() and path.stat().st_size > 4 * 1024 * 1024:
        path.replace(directory / "backend.previous.log")
    log = path.open("a", encoding="utf-8", buffering=1)
    if sys.stdout is None:
        sys.stdout = log
    if sys.stderr is None:
        sys.stderr = log


def main() -> None:
    parser = argparse.ArgumentParser(description="Xpet local backend")
    parser.add_argument("--port", type=int, default=18080)
    parser.add_argument("--data-dir", type=Path)
    parser.add_argument("--shutdown-token", default="")
    parser.add_argument("--convert-model", type=Path)
    parser.add_argument("--output-dir", type=Path)
    args = parser.parse_args()
    if not 1024 <= args.port <= 65535:
        parser.error("port must be between 1024 and 65535")
    if args.convert_model:
        if not args.output_dir:
            parser.error("--convert-model requires --output-dir")
        args.output_dir.mkdir(parents=True, exist_ok=True)
        try:
            from runtime_models import convert_model
            metrics = convert_model(args.convert_model, args.output_dir)
        except Exception as error:
            (args.output_dir / "conversion-metrics.json").write_text(json.dumps({"error": str(error)[:1200]}, ensure_ascii=False), encoding="utf-8")
            raise SystemExit(1) from None
        (args.output_dir / "conversion-metrics.json").write_text(json.dumps(metrics, ensure_ascii=False), encoding="utf-8")
        return
    if args.shutdown_token and not re.fullmatch(r"[a-f0-9]{64}", args.shutdown_token):
        parser.error("invalid launcher shutdown token")
    if args.data_dir:
        # Disposable verification starts offline without reading user credentials.
        os.environ["DESKTOP_PET_ISOLATED_DATA_DIR"] = str(args.data_dir.resolve())
    configure_windowless_logging(args.data_dir)
    import app
    import uvicorn

    config = uvicorn.Config(app.app, host="127.0.0.1", port=args.port, access_log=False,
                           log_level="info", loop="asyncio", http="h11", ws="websockets")
    server = uvicorn.Server(config)
    if args.shutdown_token:
        configure_launcher_shutdown(app.app, server, args.shutdown_token)
    server.run()


if __name__ == "__main__":
    main()

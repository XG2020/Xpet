"""Bounded, memory-only operational logs; never store request or response bodies."""
from __future__ import annotations

from collections import deque
from datetime import datetime, timezone
import re
from threading import RLock


class DiagnosticLog:
    def __init__(self, capacity: int = 1000):
        self._rows = deque(maxlen=capacity)
        self._lock = RLock()
        self._last_id = 0

    def add(self, level: str, source: str, message: str, secrets=()):
        # Messages are generated from fixed operational templates, never user text.
        for secret in secrets:
            if secret:
                message = message.replace(secret, "[已隐藏]")
        message = re.sub(r"(?i)(?:sk-(?:proj-)?[\w-]{8,}|bearer\s+\S+|(?:api[_ -]?key|password|token|secret)\s*[:=]\s*\S+)", "[已隐藏]", message)
        message = " ".join(message.split())[:1000]
        with self._lock:
            self._last_id += 1
            self._rows.append({"id": self._last_id,
                "timestamp": datetime.now(timezone.utc).isoformat(timespec="seconds"),
                "level": level.upper(), "source": source, "message": message})

    def read(self, after_id: int = 0, limit: int = 200, level: str = "") -> dict:
        with self._lock:
            rows = [dict(row) for row in self._rows if row["id"] > after_id and (not level or row["level"] == level.upper())]
            # First load gives the recent tail; subsequent requests paginate forward.
            rows = rows[-limit:] if after_id == 0 else rows[:limit]
            return {"entries": rows, "last_id": rows[-1]["id"] if rows else self._last_id}

    def clear(self):
        with self._lock:
            self._rows.clear()


diagnostic_log = DiagnosticLog()

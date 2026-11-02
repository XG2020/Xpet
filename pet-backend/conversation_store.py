from __future__ import annotations

import sqlite3
from contextlib import closing
from datetime import datetime, timezone
from pathlib import Path
from threading import RLock

from errors import ServiceError


class ConversationStore:
    """Local, opt-in conversation storage. Read operations never create a database."""

    def __init__(self, path: Path | str) -> None:
        self.path = Path(path)
        self._lock = RLock()

    def _connect(self) -> sqlite3.Connection:
        connection = sqlite3.connect(self.path, timeout=5)
        connection.row_factory = sqlite3.Row
        connection.execute("PRAGMA foreign_keys = ON")
        return connection

    @staticmethod
    def _initialize(connection: sqlite3.Connection) -> None:
        connection.executescript("""
            CREATE TABLE IF NOT EXISTS conversations (
                id TEXT PRIMARY KEY, title TEXT NOT NULL,
                created_at TEXT NOT NULL, updated_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS messages (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                session_id TEXT NOT NULL REFERENCES conversations(id) ON DELETE CASCADE,
                request_id TEXT NOT NULL, role TEXT NOT NULL CHECK(role IN ('user', 'assistant')),
                content TEXT NOT NULL, created_at TEXT NOT NULL,
                UNIQUE(session_id, request_id, role)
            );
            CREATE INDEX IF NOT EXISTS messages_session ON messages(session_id, id);
        """)
        if not ConversationStore._has_interaction_column(connection):
            connection.execute("ALTER TABLE messages ADD COLUMN interaction_zone TEXT")

    @staticmethod
    def _has_interaction_column(connection: sqlite3.Connection) -> bool:
        return any(row["name"] == "interaction_zone" for row in connection.execute("PRAGMA table_info(messages)"))

    @staticmethod
    def _storage_error() -> ServiceError:
        return ServiceError("无法访问本地会话记录，请检查文件权限和数据库状态；原有记录未被清空。", 500)

    def save_pair(self, session_id: str, request_id: str, user: str, assistant: str, interaction_zone: str | None = None) -> None:
        """Commit both messages atomically; a retried request cannot add duplicates."""
        now = datetime.now(timezone.utc).isoformat(timespec="microseconds")
        try:
            with self._lock:
                self.path.parent.mkdir(parents=True, exist_ok=True)
                with closing(self._connect()) as connection:
                    self._initialize(connection)
                    with connection:
                        connection.execute(
                            "INSERT OR IGNORE INTO conversations(id, title, created_at, updated_at) VALUES (?, ?, ?, ?)",
                            (session_id, "互动聊天" if interaction_zone else " ".join(user.split())[:60], now, now),
                        )
                        inserted = connection.execute(
                            "INSERT OR IGNORE INTO messages(session_id, request_id, role, content, created_at, interaction_zone) VALUES (?, ?, 'user', ?, ?, ?)",
                            (session_id, request_id, user, now, interaction_zone),
                        ).rowcount
                        if inserted:
                            connection.execute(
                                "INSERT INTO messages(session_id, request_id, role, content, created_at) VALUES (?, ?, 'assistant', ?, ?)",
                                (session_id, request_id, assistant, now),
                            )
                            connection.execute("UPDATE conversations SET updated_at = ? WHERE id = ?", (now, session_id))
        except (OSError, sqlite3.Error):
            raise self._storage_error() from None

    def list_conversations(self) -> list[dict]:
        try:
            with self._lock:
                if not self.path.exists():
                    return []
                with closing(self._connect()) as connection:
                    visible = "(m.role != 'user' OR m.interaction_zone IS NULL OR m.interaction_zone = '')" if self._has_interaction_column(connection) else "1"
                    rows = connection.execute(f"""
                        SELECT c.id, c.title, c.updated_at, COUNT(m.id) AS message_count
                        FROM conversations c LEFT JOIN messages m ON m.session_id = c.id AND {visible}
                        GROUP BY c.id ORDER BY c.updated_at DESC, c.id
                    """).fetchall()
                    return [dict(row) for row in rows]
        except (OSError, sqlite3.Error):
            raise self._storage_error() from None

    def get_messages(self, session_id: str, limit: int | None = None, include_interactions: bool = False) -> list[dict[str, str]] | None:
        try:
            with self._lock:
                if not self.path.exists():
                    return None
                with closing(self._connect()) as connection:
                    if connection.execute("SELECT 1 FROM conversations WHERE id = ?", (session_id,)).fetchone() is None:
                        return None
                    visible = ""
                    if not include_interactions and self._has_interaction_column(connection):
                        visible = " AND (role != 'user' OR interaction_zone IS NULL OR interaction_zone = '')"
                    if limit is None:
                        rows = connection.execute(
                            f"SELECT role, content FROM messages WHERE session_id = ?{visible} ORDER BY id", (session_id,),
                        ).fetchall()
                    else:
                        rows = connection.execute(
                            f"SELECT role, content FROM (SELECT id, role, content FROM messages WHERE session_id = ?{visible} ORDER BY id DESC LIMIT ?) ORDER BY id",
                            (session_id, limit),
                        ).fetchall()
                    return [dict(row) for row in rows]
        except (OSError, sqlite3.Error):
            raise self._storage_error() from None

    def delete_conversation(self, session_id: str) -> bool:
        try:
            with self._lock:
                if not self.path.exists():
                    return False
                with closing(self._connect()) as connection, connection:
                    # secure_delete also clears deleted message bytes from SQLite's freed cells.
                    connection.execute("PRAGMA secure_delete = ON")
                    return connection.execute("DELETE FROM conversations WHERE id = ?", (session_id,)).rowcount > 0
        except (OSError, sqlite3.Error):
            raise self._storage_error() from None

"""Character-scoped Markdown skills. Skill text never runs code or expands tools."""
from __future__ import annotations

import json
import logging
from pathlib import Path
import re
from threading import RLock
from typing import Callable
from uuid import uuid4

from fastapi import APIRouter, Query
from pydantic import BaseModel, ConfigDict, Field, field_validator

from config import DATA_DIR, LIBRARY_DIR, settings_manager
from errors import ServiceError
from resource_libraries import migrate_skill_books

MAX_IMPORT_BYTES = 64 * 1024
MAX_MARKDOWN_CHARS = 60000
MAX_MODEL_CHARS = 12000
MAX_CUSTOM_SKILLS = 64
SAFE_ID = re.compile(r"^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$")
BUILTINS = (
    {"id": "builtin_interaction", "name": "自然互动", "description": "按当前角色和好感度自然回应，尊重用户的互动选择。",
     "markdown": "# 自然互动\n\n保持当前角色的身份与说话风格，用简短口语回应。依据触碰部位与好感度选择合适反馈；用户要求停止时立即停止。不要把触碰参数、好感度数字或工具调用过程说成角色台词。继续遵循 Xpet 已有的互动边界。", "built_in": True, "enabled": True},
    {"id": "builtin_actions", "name": "动作配合", "description": "让已启用动作、表情和服饰配合当前对话。",
     "markdown": "# 动作配合\n\n先查看当前可用动作，再选择与语气和请求一致的一项。只使用角色卡中已保存的服饰和当前目录中已启用的动作；不要虚构动作、资源或执行结果。技能正文不授予新工具、脚本执行或电脑操作权限，所有工具仍受 Xpet 现有开关与确认流程约束。", "built_in": True, "enabled": True},
)


class SkillRecord(BaseModel):
    model_config = ConfigDict(extra="forbid")
    id: str = Field(pattern=r"^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$")
    name: str = Field(min_length=1, max_length=64)
    description: str = Field(default="", max_length=500)
    markdown: str = Field(min_length=1, max_length=MAX_MARKDOWN_CHARS)
    enabled: bool = True
    built_in: bool = False

    @field_validator("name", "markdown")
    @classmethod
    def nonempty(cls, value: str) -> str:
        if not value.strip() or "\0" in value:
            raise ValueError("技能名称和正文不能为空，也不能包含空字符。")
        return value.strip()


class SkillWrite(BaseModel):
    model_config = ConfigDict(extra="forbid")
    name: str | None = Field(default=None, min_length=1, max_length=64)
    description: str | None = Field(default=None, max_length=500)
    markdown: str | None = Field(default=None, min_length=1, max_length=MAX_MARKDOWN_CHARS)
    enabled: bool | None = None

    @field_validator("name", "markdown")
    @classmethod
    def nonempty(cls, value: str | None) -> str | None:
        if value is not None and (not value.strip() or "\0" in value):
            raise ValueError("技能名称和正文不能为空，也不能包含空字符。")
        return value.strip() if value is not None else None


class SkillImport(BaseModel):
    model_config = ConfigDict(extra="forbid")
    path: str = Field(min_length=1, max_length=4096)


def _resolve_character(character_id: str | None) -> str:
    active = settings_manager.get().active_character_id
    identity = character_id or active
    if not SAFE_ID.fullmatch(identity):
        raise ServiceError("角色标识无效。", 422)
    settings_manager.character(identity)
    if identity != active:
        raise ServiceError("角色已切换，请刷新技能书后重试。", 409)
    return identity


class SkillBookStore:
    def __init__(self, root: Path, resolve_character: Callable[[str | None], str], legacy_root: Path | None = None):
        self.root = root.resolve()
        self.legacy_root = legacy_root
        self.resolve_character = resolve_character
        self.lock = RLock()

    def _path(self, identity: str) -> Path:
        if not SAFE_ID.fullmatch(identity):
            raise ServiceError("角色标识无效。", 422)
        path = (self.root / (identity + ".json")).resolve()
        if path.parent != self.root:
            raise ServiceError("技能书路径无效。", 422)
        return path

    def _load(self, identity: str) -> list[dict]:
        builtins = [dict(item) for item in BUILTINS]
        path = self._path(identity)
        if self.legacy_root is not None and not path.exists():
            migrate_skill_books(self.legacy_root, self.root)
        if not path.exists():
            return builtins
        try:
            if path.stat().st_size > 5 * 1024 * 1024:
                raise ValueError()
            data = json.loads(path.read_text(encoding="utf-8-sig"))
            if not isinstance(data, dict) or data.get("version") != 1 or not isinstance(data.get("skills"), list):
                raise ValueError()
            if len(data["skills"]) > MAX_CUSTOM_SKILLS + len(BUILTINS):
                raise ValueError()
            seen = set()
            custom = []
            lookup = {item["id"]: item for item in builtins}
            for row in data["skills"]:
                record = SkillRecord.model_validate(row).model_dump()
                if record["id"] in seen:
                    raise ValueError()
                seen.add(record["id"])
                if record["id"] in lookup:
                    lookup[record["id"]]["enabled"] = record["enabled"]
                elif record["id"].startswith("skill_") and not record["built_in"]:
                    custom.append(record)
                else:
                    raise ValueError()
            if len(custom) > MAX_CUSTOM_SKILLS:
                raise ValueError()
            return builtins + custom
        except (OSError, ValueError, TypeError):
            raise ServiceError("无法读取技能书，请修复本机技能文件；原文件未修改。", 500) from None

    def _save(self, identity: str, records: list[dict]) -> None:
        self.resolve_character(identity)
        path = self._path(identity)
        self.root.mkdir(parents=True, exist_ok=True)
        temporary = self.root / (identity + "." + uuid4().hex + ".tmp")
        try:
            temporary.write_text(json.dumps({"version": 1, "skills": records}, ensure_ascii=False, indent=2), encoding="utf-8")
            temporary.replace(path)
        finally:
            if temporary.exists():
                temporary.unlink()
        try:
            self._export_markdown(identity, records)
        except OSError:
            logging.getLogger(__name__).warning("技能 Markdown 副本导出暂时失败，技能书已经保存。")

    def _export_markdown(self, identity: str, records: list[dict]) -> None:
        """Readable copies; the character JSON remains authoritative."""
        directory = self.root / identity
        directory.mkdir(parents=True, exist_ok=True)
        for record in records:
            if not SAFE_ID.fullmatch(record["id"]):
                raise ServiceError("技能标识无效。", 422)
            target = directory / (record["id"] + ".md")
            temporary = directory / (record["id"] + "." + uuid4().hex + ".tmp")
            try:
                temporary.write_text(record["markdown"] + "\n", encoding="utf-8")
                temporary.replace(target)
            finally:
                temporary.unlink(missing_ok=True)

    @staticmethod
    def metadata(record: dict) -> dict:
        return {key: record[key] for key in ("id", "name", "description", "enabled", "built_in")}

    def list(self, character_id: str | None = None, enabled_only: bool = False) -> list[dict]:
        with self.lock:
            identity = self.resolve_character(character_id)
            records = self._load(identity)
            return [self.metadata(row) for row in records if row["enabled"] or not enabled_only]

    def read(self, skill_id: str, character_id: str | None = None, enabled_only: bool = False) -> dict:
        if not SAFE_ID.fullmatch(skill_id):
            raise ServiceError("技能标识无效。", 422)
        with self.lock:
            identity = self.resolve_character(character_id)
            record = next((item for item in self._load(identity) if item["id"] == skill_id), None)
            if record is None:
                raise ServiceError("找不到这个技能。", 404)
            if enabled_only and not record["enabled"]:
                raise ServiceError("此技能已关闭。", 409)
            return dict(record)

    def create(self, payload: SkillWrite, character_id: str | None = None) -> dict:
        with self.lock:
            identity = self.resolve_character(character_id)
            records = self._load(identity)
            if sum(not item["built_in"] for item in records) >= MAX_CUSTOM_SKILLS:
                raise ServiceError("每个角色最多保存 64 个自定义技能。", 422)
            if payload.name is None or payload.markdown is None:
                raise ServiceError("请填写技能名称和正文。", 422)
            record = SkillRecord(id="skill_" + uuid4().hex, **payload.model_dump(exclude_none=True)).model_dump()
            records.append(record)
            self._save(identity, records)
            return record

    def update(self, skill_id: str, payload: SkillWrite, character_id: str | None = None) -> dict:
        if not SAFE_ID.fullmatch(skill_id):
            raise ServiceError("技能标识无效。", 422)
        with self.lock:
            identity = self.resolve_character(character_id)
            records = self._load(identity)
            index = next((i for i, item in enumerate(records) if item["id"] == skill_id), None)
            if index is None:
                raise ServiceError("找不到这个技能。", 404)
            previous = records[index]
            changes = payload.model_dump(exclude_none=True)
            if previous["built_in"] and any(key != "enabled" and value != previous.get(key) for key, value in changes.items()):
                raise ServiceError("内置技能可以启用或关闭，内容请另存为新技能。", 409)
            records[index] = SkillRecord.model_validate({**previous, **changes}).model_dump()
            self._save(identity, records)
            return dict(records[index])

    def delete(self, skill_id: str, character_id: str | None = None) -> None:
        if not SAFE_ID.fullmatch(skill_id):
            raise ServiceError("技能标识无效。", 422)
        with self.lock:
            identity = self.resolve_character(character_id)
            records = self._load(identity)
            found = next((row for row in records if row["id"] == skill_id), None)
            if found is None:
                raise ServiceError("找不到这个技能。", 404)
            if found["built_in"]:
                raise ServiceError("内置技能不可删除，可将它关闭。", 409)
            self._save(identity, [row for row in records if row["id"] != skill_id])

    def import_file(self, raw_path: str, character_id: str | None = None) -> dict:
        identity = self.resolve_character(character_id)
        source = Path(raw_path)
        if not source.is_absolute() or raw_path.startswith(("\\\\", "//")) or ".." in source.parts:
            raise ServiceError("请选择本机 Markdown 文件的完整路径。", 422)
        source = source.resolve()
        if source.suffix.lower() != ".md" or not source.is_file():
            raise ServiceError("请选择 SKILL.md 或其他 Markdown 文件。", 422)
        if source.stat().st_size > MAX_IMPORT_BYTES:
            raise ServiceError("技能文件不能超过 64 KB。", 422)
        try:
            with source.open("rb") as stream:
                raw = stream.read(MAX_IMPORT_BYTES + 1)
            if len(raw) > MAX_IMPORT_BYTES:
                raise ServiceError("技能文件不能超过 64 KB。", 422)
            markdown = raw.decode("utf-8-sig")
        except (OSError, UnicodeError):
            raise ServiceError("技能文件需使用 UTF-8 编码。", 422) from None
        if "\0" in markdown or not markdown.strip() or len(markdown) > MAX_MARKDOWN_CHARS:
            raise ServiceError("技能正文为空或格式无效。", 422)
        name, description = source.stem, ""
        if markdown.startswith("---\n") or markdown.startswith("---\r\n"):
            lines = markdown.splitlines()
            end = next((i for i in range(1, min(len(lines), 80)) if lines[i].strip() == "---"), None)
            if end is not None:
                for line in lines[1:end]:
                    match = re.fullmatch(r"(name|description):\s*(.+)", line)
                    if match:
                        value = match[2].strip().strip("\"'")
                        if value not in {"|", ">", "|-", ">-"}:
                            if match[1] == "name": name = value
                            else: description = value
        if not description:
            description = next((line.strip().lstrip("# ") for line in markdown.splitlines() if line.strip() and line.strip() != "---"), "")
        return self.create(SkillWrite(name=name[:64] or "新技能", description=description[:500], markdown=markdown), identity)


store = SkillBookStore(LIBRARY_DIR / "pet-skills", _resolve_character, DATA_DIR / "skill_books")
router = APIRouter(prefix="/api/skill-book")


def get_available_skills(character_id: str | None = None) -> list[dict]:
    """Progressive discovery gives the model metadata, never every skill body."""
    return store.list(character_id, enabled_only=True)


def read_skill(skill_id: str, character_id: str | None = None) -> dict:
    record = store.read(skill_id, character_id, enabled_only=True)
    record["truncated"] = len(record["markdown"]) > MAX_MODEL_CHARS
    record["markdown"] = record["markdown"][:MAX_MODEL_CHARS]
    return record

def get_skill_prompt_context(character_id: str | None = None, metadata: list[dict] | None = None) -> list[dict]:
    """Teach every enabled skill with a fair per-skill budget and progressive overflow."""
    with store.lock:
        identity = store.resolve_character(character_id)
        allowed = {item["id"] for item in metadata} if metadata is not None else None
        records = [row for row in store._load(identity) if row["enabled"] and (allowed is None or row["id"] in allowed)]
        limit = min(MAX_MODEL_CHARS, max(256, 24000 // max(1, len(records))))
        return [{**store.metadata(row), "markdown": row["markdown"][:limit], "truncated": len(row["markdown"]) > limit} for row in records]


@router.get("")
def list_book(character_id: str | None = Query(default=None, max_length=64)):
    identity = store.resolve_character(character_id)
    return {"character_id": identity, "skills": store.list(identity)}


@router.post("/import")
def import_book(payload: SkillImport, character_id: str | None = Query(default=None, max_length=64)):
    identity = store.resolve_character(character_id)
    return {"character_id": identity, "skill": store.import_file(payload.path, identity)}


@router.post("")
def create_book(payload: SkillWrite, character_id: str | None = Query(default=None, max_length=64)):
    identity = store.resolve_character(character_id)
    return {"character_id": identity, "skill": store.create(payload, identity)}


@router.get("/{skill_id}")
def get_book(skill_id: str, character_id: str | None = Query(default=None, max_length=64)):
    identity = store.resolve_character(character_id)
    return {"character_id": identity, "skill": store.read(skill_id, identity)}


@router.put("/{skill_id}")
def update_book(skill_id: str, payload: SkillWrite, character_id: str | None = Query(default=None, max_length=64)):
    identity = store.resolve_character(character_id)
    return {"character_id": identity, "skill": store.update(skill_id, payload, identity)}


@router.delete("/{skill_id}")
def delete_book(skill_id: str, character_id: str | None = Query(default=None, max_length=64)):
    identity = store.resolve_character(character_id)
    store.delete(skill_id, identity)
    return {"character_id": identity, "ok": True, "id": skill_id}

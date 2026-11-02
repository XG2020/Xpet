"""Portable resource paths and additive migration, independent of runtime settings."""
from __future__ import annotations

import json
import filecmp
import os
from pathlib import Path
import re
import shutil
import tempfile

SAFE_ID = re.compile(r"^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$")
MOTION_NAME = re.compile(r"^[a-f0-9]{64}\.vmd$")
ACTION_FIELDS = ("id", "label", "enabled", "builtIn", "speed", "trigger", "ai_decide",
                 "steps", "motion_file", "source_format", "duration")


def library_root(backend_dir: Path) -> Path:
    isolated = os.environ.get("DESKTOP_PET_ISOLATED_DATA_DIR", "").strip()
    return Path(isolated).resolve() if isolated else backend_dir.resolve().parent


def legacy_motion_directories() -> tuple[Path, ...]:
    local = Path(os.environ.get("LOCALAPPDATA", str(Path.home() / "AppData" / "Local")))
    roots = (local.parent / "LocalLow" / "DesktopPet", local / "DesktopPet")
    return tuple(root / product / "pet-motions" for root in roots for product in ("Xpet", "Lita Desktop Pet"))


def copy_missing(source: Path, target: Path) -> bool:
    """Copy one regular file exclusively. Never replace or remove a legacy source."""
    if source.is_symlink() or not source.is_file() or target.exists():
        return False
    target.parent.mkdir(parents=True, exist_ok=True)
    created = False
    try:
        with source.open("rb") as incoming, target.open("xb") as outgoing:
            created = True
            shutil.copyfileobj(incoming, outgoing)
        return True
    except FileExistsError:
        return False
    except OSError:
        if created:
            target.unlink(missing_ok=True)
        raise


def migrate_skill_books(source: Path, target: Path) -> int:
    """Retain character IDs and preserve any destination conflict in .legacy."""
    if not source.is_dir() or source.is_symlink() or source.resolve() == target.resolve():
        return 0
    copied = 0
    for entry in source.glob("*.json"):
        if not SAFE_ID.fullmatch(entry.stem) or entry.is_symlink() or entry.stat().st_size > 5 * 1024 * 1024:
            continue
        destination = target / entry.name
        if destination.exists():
            if not destination.is_symlink() and filecmp.cmp(entry, destination, shallow=False):
                continue
            destination = target / ".legacy" / "skill_books" / entry.name
        copied += int(copy_missing(entry, destination))
    return copied


def migrate_resource_libraries(backend_dir: Path, root: Path | None = None,
                               motion_sources: tuple[Path, ...] | None = None) -> None:
    root = (root or library_root(backend_dir)).resolve()
    for name in ("pet-models", "pet-actions", "pet-skills"):
        (root / name).mkdir(parents=True, exist_ok=True)
    # QA must never discover or copy the current user's resource libraries.
    isolated = os.environ.get("DESKTOP_PET_ISOLATED_DATA_DIR", "").strip()
    if isolated and motion_sources is None:
        sources = (root / "pet-motions",)
        legacy_skills = root / "skill_books"
    else:
        sources = motion_sources if motion_sources is not None else legacy_motion_directories()
        legacy_skills = backend_dir / "skill_books"
    migrate_skill_books(legacy_skills, root / "pet-skills")
    for source in sources:
        if source.is_symlink() or not source.is_dir():
            continue
        for entry in source.glob("*.vmd"):
            if MOTION_NAME.fullmatch(entry.name) and not entry.is_symlink() and entry.stat().st_size <= 32 * 1024 * 1024:
                copy_missing(entry, root / "pet-actions" / entry.name)


def write_json_atomic(path: Path, data: dict) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    fd, temporary = tempfile.mkstemp(prefix=".library-", suffix=".tmp", dir=path.parent)
    try:
        with os.fdopen(fd, "w", encoding="utf-8") as output:
            json.dump(data, output, ensure_ascii=False, indent=2)
            output.flush()
            os.fsync(output.fileno())
        os.replace(temporary, path)
    finally:
        if os.path.exists(temporary):
            os.unlink(temporary)


def export_character_actions(root: Path, cards) -> None:
    """Export only action definitions from in-memory cards, never whole settings."""
    for card in cards:
        if not SAFE_ID.fullmatch(card.id):
            raise ValueError("角色动作库标识无效。")
        actions = []
        for action in card.actions:
            record = action.model_dump(mode="json", include=set(ACTION_FIELDS))
            record["steps"] = [{"action": step.action, "seconds": step.seconds} for step in action.steps]
            actions.append(record)
        write_json_atomic(root / "pet-actions" / (card.id + ".json"),
                          {"version": 1, "character_id": card.id, "actions": actions})

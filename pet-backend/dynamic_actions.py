"""Per-reply action selection. Tools validate intent; the final reply owns playback."""
from __future__ import annotations

import hashlib
import json
import os
from pathlib import Path
from threading import RLock
import time
from typing import Callable, get_args

from fastapi import APIRouter
from pydantic import BaseModel, Field
from config import BACKEND_DIR, LIBRARY_DIR, settings_manager
from errors import ServiceError
from schemas import CHARACTER_ID_PATTERN, ChatResponse, PetAction, RuntimeSettings

RETIRED_BUILTINS = frozenset({"hold_sword", "draw_slash"})
# Legacy schema values still parse saved settings, but are never callable.
BUILTINS = tuple(name for name in get_args(PetAction) if name not in RETIRED_BUILTINS)
NON_AUTONOMOUS = {"guard", "drag"}
LABELS = dict(zip(BUILTINS, ("自然待机", "挥手", "开心", "思考", "难过", "惊讶", "说话", "走路", "点头", "歪头", "害羞", "伸懒腰", "庆祝", "环顾", "后退遮挡", "跳跃", "跑步", "收腿避让", "轻移重心", "小幅摆腿", "生气跺脚")))


def motion_directory() -> Path:
    from resource_libraries import library_root
    return library_root(BACKEND_DIR) / "pet-actions"


def fingerprint(value: RuntimeSettings) -> str:
    data = {"id": value.active_character_id, "model": value.model_id, "skin": value.active_skin_id,
            "actions": [item.model_dump() for item in value.actions]}
    return hashlib.sha256(json.dumps(data, sort_keys=True, ensure_ascii=False).encode()).hexdigest()


class RuntimeCapabilities(BaseModel):
    character_id: str = Field(pattern=CHARACTER_ID_PATTERN)
    model_id: str = Field(default="builtin-lita", max_length=100, pattern=r"^[A-Za-z0-9_-]+$")
    actions: list[str] = Field(default_factory=list, max_length=88)
    has_sword: bool = False
    model_ready: bool = False


class ActionCatalog:
    def __init__(self, workspace: Path = LIBRARY_DIR, motions: Path | None = None):
        self.workspace = workspace
        self.motions = motions
        self.capabilities: dict[str, tuple[float, RuntimeCapabilities]] = {}
        self.lock = RLock()

    def report(self, data: RuntimeCapabilities):
        live = settings_manager.get()
        if data.character_id != live.active_character_id or data.model_id != (live.model_id or "builtin-lita"):
            raise ServiceError("角色或模型已切换，请重新报告动作能力。", 409)
        with self.lock:
            self.capabilities[data.character_id] = (time.monotonic(), data)

    def available(self, value: RuntimeSettings) -> list[dict]:
        model_id = value.model_id or "builtin-lita"
        with self.lock:
            reported = self.capabilities.get(value.active_character_id)
        caps = reported[1] if reported and time.monotonic() - reported[0] < 100 and reported[1].model_id == model_id else None
        if caps is not None and not caps.model_ready:
            return []
        if model_id != "builtin-lita":
            # A bundle exists only after the selected client has verified its model.
            if caps is None or not (self.workspace / "pet-models" / model_id / "model.json").is_file():
                return []
            try:
                directory = (self.workspace / "pet-models" / model_id).resolve()
                metadata = json.loads((directory / "model.json").read_text(encoding="utf-8-sig"))
                model = (directory / metadata["bundle_file"]).resolve()
                if model.parent != directory or model.suffix not in {".petmodel", ".petraw"} or not model.is_file(): return []
            except (OSError, ValueError, KeyError, TypeError):
                return []
        configured = {item.id: item for item in value.actions}
        enabled = {name for name in BUILTINS if name not in configured or configured[name].enabled}
        if caps is not None:
            enabled.intersection_update(caps.actions)
        result = [{"id": name, "name": configured[name].label or LABELS.get(name, name) if name in configured else LABELS.get(name, name),
                   "kind": "builtin"} for name in BUILTINS if name in enabled and name not in NON_AUTONOMOUS]
        for entry in value.actions:
            if not entry.id.startswith("custom_") or not entry.enabled or not entry.ai_decide:
                continue
            if entry.trigger in RETIRED_BUILTINS or any(step.action in RETIRED_BUILTINS for step in entry.steps):
                continue
            if caps is not None and entry.id not in caps.actions:
                continue
            if entry.motion_file:
                path = (self.motions or motion_directory()) / entry.motion_file
                try:
                    if not path.is_file() or path.stat().st_size > 32 * 1024 * 1024:
                        continue
                    with path.open("rb") as file:
                        if not file.read(30).startswith(b"Vocaloid Motion Data 0002"):
                            continue
                except OSError:
                    continue
            elif not any(step.action in enabled and step.action not in NON_AUTONOMOUS for step in entry.steps):
                continue
            result.append({"id": entry.id, "name": entry.label, "kind": "vmd" if entry.motion_file else "combination",
                           "duration": entry.duration if entry.motion_file else sum(step.seconds for step in entry.steps),
                           "speed": entry.speed})
        return result


catalog = ActionCatalog()


class ActionBroker:
    def __init__(self, value: RuntimeSettings, live: Callable[[], RuntimeSettings], *, actions: ActionCatalog = catalog,
                 interaction_zone: str | None = None, affinity: int | None = None):
        self.value = value
        self.live = live
        self.actions = actions
        self.origin = fingerprint(value)
        self.protected = interaction_zone == "leg" or (interaction_zone in {"chest", "waist", "lower_body"} and (affinity if affinity is not None else 20) < 30)
        self.selected = ""

    def is_current(self) -> bool:
        try:
            return fingerprint(self.live()) == self.origin
        except Exception:
            return False

    def get_available_actions(self) -> list[dict]:
        return self.actions.available(self.value) if self.is_current() and not self.protected else []

    def play_action(self, action_id: str) -> dict:
        if action_id not in {item["id"] for item in self.get_available_actions()}:
            return {"ok": False, "error": "动作已关闭、未授权、模型/资源不可用或角色已切换。"}
        if self.selected and self.selected != action_id:
            return {"ok": False, "error": "本轮已经选择一个动作，请完成当前回复。"}
        self.selected = action_id
        return {"ok": True, "status": "deferred_until_final_reply", "action_id": action_id,
                "message": "已选择动作；客户端收到完整回复并再次验证后播放，尚未执行。"}

    def finish(self, response: ChatResponse) -> ChatResponse:
        requested = self.selected or response.action_id
        valid = {item["id"] for item in self.get_available_actions()}
        response.action_id = requested if requested in valid else ""
        if response.action_id:
            response.action = response.action_id if response.action_id in BUILTINS else "idle"
        elif requested or (response.action not in valid and response.action not in NON_AUTONOMOUS):
            response.action = "idle"
        return response


router = APIRouter(prefix="/api/pet-actions")


@router.get("")
def get_available_actions():
    value = settings_manager.get()
    return {"character_id": value.active_character_id, "actions": catalog.available(value)}


@router.post("/capabilities")
def report_runtime_actions(data: RuntimeCapabilities):
    catalog.report(data)
    return {"ok": True}


class PlayActionRequest(BaseModel):
    character_id: str = Field(pattern=CHARACTER_ID_PATTERN)
    action_id: str = Field(min_length=1, max_length=100, pattern=r"^[a-z0-9_]+$")


@router.post("/play")
def play_action(request: PlayActionRequest):
    value = settings_manager.get()
    if value.active_character_id != request.character_id:
        raise ServiceError("角色已切换，请重新获取动作列表。", 409)
    return ActionBroker(value, settings_manager.get).play_action(request.action_id)

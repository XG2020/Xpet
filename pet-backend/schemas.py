from __future__ import annotations

from typing import Any, Literal
from urllib.parse import urlsplit
from pydantic import BaseModel, Field, PrivateAttr, field_validator

PetAction = Literal[
    "idle", "wave", "happy", "think", "sad", "surprised", "speak", "walk",
    "nod", "tilt_head", "shy", "stretch", "cheer", "look_around", "guard", "jump", "run", "hold_sword", "draw_slash", "leg_retract", "leg_shift", "leg_sway", "angry_stomp",
]
PetEmotion = Literal["neutral", "happy", "curious", "thinking", "sad", "excited", "angry"]
CommandType = Literal["open_app", "open_url", "screen_analyze", "tts_speak", "noop"]


class ActionCommand(BaseModel):
    type: CommandType = "noop"
    target: str | None = Field(default=None, max_length=4096)
    args: dict[str, Any] = Field(default_factory=dict)


CHARACTER_ID_PATTERN = r"^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$"


class CharacterActionStep(BaseModel):
    action: PetAction
    seconds: float = Field(default=2, ge=0.5, le=10, allow_inf_nan=False)


class CharacterSkin(BaseModel):
    id: str = Field(pattern=CHARACTER_ID_PATTERN)
    name: str = Field(min_length=1, max_length=48)
    model_id: str = Field(default="", max_length=100, pattern=r"^[A-Za-z0-9_-]*$")

    @field_validator("name")
    @classmethod
    def nonempty_name(cls, value: str) -> str:
        if not value.strip():
            raise ValueError("皮肤名称不能为空。")
        return value.strip()


class CharacterAction(BaseModel):
    id: str = Field(min_length=1, max_length=100, pattern=r"^[a-z0-9_]+$")
    label: str = Field(default="", max_length=24)
    enabled: bool = True
    builtIn: bool = False
    speed: float = Field(default=1, ge=0.5, le=2, allow_inf_nan=False)
    trigger: str = Field(default="", max_length=30)
    ai_decide: bool = False
    steps: list[CharacterActionStep] = Field(default_factory=list, max_length=8)
    motion_file: str = Field(default="", max_length=68)
    source_format: Literal["", "vmd"] = ""
    duration: float = Field(default=0, ge=0, le=180, allow_inf_nan=False)

    @field_validator("motion_file")
    @classmethod
    def validate_motion_file(cls, value: str) -> str:
        import re
        if value and not re.fullmatch(r"[a-f0-9]{64}\.vmd", value):
            raise ValueError("导入动作文件标识无效。")
        return value

    @field_validator("id")
    @classmethod
    def validate_id(cls, value: str) -> str:
        builtins = {"idle", "wave", "happy", "think", "sad", "surprised", "speak", "walk", "nod", "tilt_head", "shy", "stretch", "cheer", "look_around", "guard", "drag", "jump", "run", "hold_sword", "draw_slash", "leg_retract", "leg_shift", "leg_sway", "angry_stomp"}
        if value not in builtins and not value.startswith("custom_"):
            raise ValueError("动作标识无效。")
        return value


class SettingsFields(BaseModel):
    active_character_id: str = Field(default="default", pattern=CHARACTER_ID_PATTERN)
    model_id: str = Field(default="", max_length=100, pattern=r"^[A-Za-z0-9_-]*$")
    skins: list[CharacterSkin] = Field(default_factory=list, max_length=32)
    active_skin_id: str = Field(default="default", pattern=CHARACTER_ID_PATTERN)
    allow_ai_skin_switch: bool = True
    actions: list[CharacterAction] = Field(default_factory=list, max_length=88)
    pet_name: str = Field(default="Lita", min_length=1, max_length=64)
    pet_persona: str = Field(default="你是 3D 桌面宠物丽塔，说话自然、温柔、稍微俏皮，回答简洁，根据对话输出动作和情绪。", min_length=1, max_length=8000)
    llm_provider: Literal["openai", "openai-compatible", "anthropic", "systemone", "ollama", "offline", "mock"] = "openai"
    llm_model: str = Field(default="gpt-4.1-mini", min_length=1, max_length=200)
    openai_api_key: str = Field(default="", max_length=2048, repr=False)
    openai_base_url: str = Field(default="", max_length=2048)
    enable_real_rpa: bool = False
    save_conversation_history: bool = False
    enable_long_term_memory: bool = False
    enable_proactive_screen: bool = False
    proactive_screen_interval_seconds: int = Field(default=90, ge=30, le=900)
    allowed_apps: list[str] = Field(default_factory=lambda: ["notepad", "calc", "explorer", "msedge"], max_length=32)
    default_tts_voice: str = Field(default="zh-CN-XiaoxiaoNeural", min_length=1, max_length=100)
    tts_provider: Literal["edge", "openai", "gpt_sovits"] = "edge"
    tts_base_url: str = Field(default="", max_length=2048)
    tts_api_key: str = Field(default="", max_length=2048, repr=False)
    tts_model: str = Field(default="gpt-4o-mini-tts", min_length=1, max_length=200)
    tts_voice: str = Field(default="alloy", min_length=1, max_length=100)
    asr_provider: Literal["openai", "windows"] = "openai"
    windows_asr_language: str = Field(default="zh-CN", min_length=2, max_length=30, pattern=r"^[A-Za-z]{2,3}(?:-[A-Za-z0-9]{2,8}){0,2}$")
    asr_base_url: str = Field(default="", max_length=2048)
    asr_api_key: str = Field(default="", max_length=2048, repr=False)
    asr_model: str = Field(default="whisper-1", min_length=1, max_length=200)
    gpt_sovits_ref_audio_path: str = Field(default="", max_length=4096)
    gpt_sovits_prompt_text: str = Field(default="", max_length=4000)
    gpt_sovits_prompt_lang: str = Field(default="zh", min_length=1, max_length=30)
    gpt_sovits_text_lang: str = Field(default="zh", min_length=1, max_length=30)

    @field_validator("openai_base_url", "tts_base_url", "asr_base_url")
    @classmethod
    def validate_base_url(cls, value: str) -> str:
        value = value.strip().rstrip("/")
        if value:
            parsed = urlsplit(value)
            if parsed.scheme not in ("http", "https") or not parsed.hostname or parsed.username or parsed.password or parsed.fragment or parsed.query:
                raise ValueError("接口地址必须是 http(s) URL，且不能包含用户名、密码、查询参数或片段。")
        return value

    @field_validator("allowed_apps")
    @classmethod
    def validate_apps(cls, value: list[str]) -> list[str]:
        apps = list(dict.fromkeys(app.strip().lower() for app in value if app.strip()))
        if any(app not in {"notepad", "calc", "explorer", "msedge"} for app in apps):
            raise ValueError("应用白名单仅支持 notepad、calc、explorer、msedge。")
        return apps

    @field_validator("actions")
    @classmethod
    def validate_actions(cls, value: list[CharacterAction]) -> list[CharacterAction]:
        if len({item.id for item in value}) != len(value):
            raise ValueError("每种动作只能配置一次。")
        composable = {"wave", "happy", "think", "sad", "surprised", "speak", "walk", "nod", "tilt_head", "shy", "stretch", "cheer", "look_around", "jump", "run", "hold_sword", "draw_slash", "leg_retract", "leg_shift", "leg_sway", "angry_stomp"}
        if sum(item.id.startswith("custom_") for item in value) > 64:
            raise ValueError("最多保存 64 个自定义动作。")
        triggers = set()
        for item in value:
            item.builtIn = not item.id.startswith("custom_")
            if item.builtIn:
                item.steps = []
                item.trigger = ""
                item.speed = 1
                item.motion_file = ""
                item.source_format = ""
                item.duration = 0
            elif item.motion_file:
                if not item.label.strip() or item.source_format != "vmd" or item.duration <= 0 or item.steps:
                    raise ValueError("导入动作需要名称、VMD 文件标识和有效时长，不可同时包含组合步骤。")
            elif not item.label.strip() or not item.steps or item.source_format or item.duration or any(step.action not in composable | {"idle"} for step in item.steps):
                raise ValueError("自定义动作需要名称及 1～8 个可组合步骤。")
            if item.trigger and (item.trigger not in composable or item.trigger in triggers):
                raise ValueError("自定义动作触发项无效或重复。")
            if item.ai_decide and item.trigger:
                raise ValueError("由模型决定和固定动作触发不能同时启用。")
            if item.trigger:
                triggers.add(item.trigger)
        return value

    @field_validator("pet_name", "pet_persona")
    @classmethod
    def validate_identity(cls, value: str) -> str:
        if not value.strip():
            raise ValueError("名称和人设不能为空。")
        return value.strip()


class RuntimeSettings(SettingsFields):
    """Internal settings; never return this object from an endpoint."""


class PublicRuntimeSettings(SettingsFields):
    openai_api_key_configured: bool = False
    tts_api_key_configured: bool = False
    asr_api_key_configured: bool = False


class RuntimeSettingsUpdate(BaseModel):
    active_character_id: str | None = Field(default=None, pattern=CHARACTER_ID_PATTERN)
    model_id: str | None = None
    actions: list[CharacterAction] | None = None
    pet_name: str | None = None
    pet_persona: str | None = None
    llm_provider: str | None = None
    llm_model: str | None = None
    openai_api_key: str | None = Field(default=None, repr=False)
    openai_base_url: str | None = None
    enable_real_rpa: bool | None = None
    save_conversation_history: bool | None = None
    enable_long_term_memory: bool | None = None
    enable_proactive_screen: bool | None = None
    proactive_screen_interval_seconds: int | None = Field(default=None, ge=30, le=900)
    allowed_apps: list[str] | None = None
    default_tts_voice: str | None = None
    tts_provider: str | None = None
    tts_base_url: str | None = None
    tts_api_key: str | None = Field(default=None, repr=False)
    tts_model: str | None = None
    tts_voice: str | None = None
    asr_provider: str | None = None
    windows_asr_language: str | None = None
    asr_base_url: str | None = None
    asr_api_key: str | None = Field(default=None, repr=False)
    asr_model: str | None = None
    gpt_sovits_ref_audio_path: str | None = None
    gpt_sovits_prompt_text: str | None = None
    gpt_sovits_prompt_lang: str | None = None
    gpt_sovits_text_lang: str | None = None
    clear_openai_api_key: bool = False
    clear_tts_api_key: bool = False
    clear_asr_api_key: bool = False


class ChatRequest(BaseModel):
    character_id: str | None = Field(default=None, pattern=CHARACTER_ID_PATTERN)
    _runtime: RuntimeSettings | None = PrivateAttr(default=None)
    message: str = Field(..., min_length=1, max_length=4000)
    request_id: str | None = Field(default=None, min_length=1, max_length=128)
    session_id: str | None = Field(default=None, min_length=1, max_length=128)
    user_id: str = Field(default="local-user", max_length=100)
    include_screen_context: bool = False
    conversation_history: list[dict[str, str]] = Field(default_factory=list, max_length=40)
    interaction_zone: Literal["head", "shoulder", "hand", "arm", "chest", "waist", "lower_body", "leg"] | None = None
    affinity: int | None = Field(default=None, ge=0, le=100)

    @field_validator("interaction_zone", mode="before")
    @classmethod
    def validate_interaction_zone(cls, value):
        # Unity JsonUtility serializes an unset string as an empty string.
        return None if isinstance(value, str) and not value.strip() else value

    @field_validator("message")
    @classmethod
    def validate_message(cls, value: str) -> str:
        if not value.strip():
            raise ValueError("消息不能为空。")
        return value.strip()

    @field_validator("conversation_history")
    @classmethod
    def validate_history(cls, value: list[dict[str, str]]) -> list[dict[str, str]]:
        if any(item.get("role") not in {"user", "assistant"} or len(item.get("content", "")) > 8000 for item in value):
            raise ValueError("历史记录只能包含 user/assistant 消息，单条最多 8000 字符。")
        return value


class ChatResponse(BaseModel):
    character_id: str = Field(default="default", pattern=CHARACTER_ID_PATTERN)
    text: str = Field(min_length=1, max_length=16000)
    request_id: str | None = Field(default=None, min_length=1, max_length=128)
    session_id: str | None = Field(default=None, min_length=1, max_length=128)
    action: PetAction = "idle"
    action_id: str = Field(default="", max_length=100, pattern=r"^[a-z0-9_]*$")
    emotion: PetEmotion = "neutral"
    command: ActionCommand = Field(default_factory=ActionCommand)
    audio_text: str | None = None
    mode: Literal["online", "offline"] = "online"
    skin_id: str = Field(default="", max_length=64)


class ExecuteActionRequest(BaseModel):
    command: ActionCommand


class ProactiveScreenRequest(BaseModel):
    request_id: str = Field(min_length=1, max_length=128, pattern=r"^[A-Za-z0-9_-]+$")


class ProactiveScreenResponse(BaseModel):
    request_id: str
    status: Literal["speak", "silent", "busy", "rate_limited", "discarded"] = "silent"
    text: str = Field(default="", max_length=300)
    action: PetAction = "idle"
    emotion: PetEmotion = "neutral"
    retry_after_seconds: int = Field(default=0, ge=0, le=900)


class MemoryWriteRequest(BaseModel):
    key: str = Field(min_length=1, max_length=100)
    content: str = Field(min_length=1, max_length=1000)
    category: str = Field(default="user", min_length=1, max_length=40)
    perspective: Literal["user", "self"] = "user"

    @field_validator("key", "content", "category")
    @classmethod
    def validate_memory_text(cls, value: str) -> str:
        if not value.strip():
            raise ValueError("记忆字段不能为空。")
        return value.strip()


class SpeechRequest(BaseModel):
    text: str = Field(min_length=1, max_length=3000)
    voice: str | None = Field(default=None, max_length=100)
    character_id: str | None = Field(default=None, pattern=CHARACTER_ID_PATTERN)


class FileAnalyzeRequest(BaseModel):
    character_id: str | None = Field(default=None, pattern=CHARACTER_ID_PATTERN)
    path: str = Field(min_length=1, max_length=4096)
    message: str = Field(default="请看看这个文件，并简要告诉我它的内容。", min_length=1, max_length=4000)


class BackendEvent(BaseModel):
    type: Literal["connected", "chat_response", "action_result", "settings", "error", "pong"]
    payload: dict[str, Any] = Field(default_factory=dict)

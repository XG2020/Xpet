"""Character card schema and conversion; credentials stay in the settings vault."""
from __future__ import annotations

from pydantic import BaseModel, Field, field_validator, model_validator

from schemas import CHARACTER_ID_PATTERN, CharacterAction, CharacterSkin, RuntimeSettings, SettingsFields

VOICE_FIELDS = (
    "default_tts_voice", "tts_provider", "tts_base_url", "tts_api_key", "tts_model", "tts_voice",
    "asr_provider", "windows_asr_language", "asr_base_url", "asr_api_key", "asr_model", "gpt_sovits_ref_audio_path", "gpt_sovits_prompt_text",
    "gpt_sovits_prompt_lang", "gpt_sovits_text_lang",
)
CARD_SETTING_FIELDS = ("model_id", "skins", "active_skin_id", "allow_ai_skin_switch", "actions", *VOICE_FIELDS)
CARD_CREDENTIAL_FIELDS = ("tts_api_key", "asr_api_key")


class CharacterCard(BaseModel):
    id: str = Field(pattern=CHARACTER_ID_PATTERN)
    name: str = Field(min_length=1, max_length=64)
    persona: str = Field(min_length=1, max_length=8000)
    model_id: str = SettingsFields.model_fields["model_id"]
    skins: list[CharacterSkin] = Field(default_factory=list, max_length=32)
    active_skin_id: str = Field(default="default", pattern=CHARACTER_ID_PATTERN)
    allow_ai_skin_switch: bool = True
    actions: list[CharacterAction] = Field(default_factory=list, max_length=88)
    default_tts_voice: str = SettingsFields.model_fields["default_tts_voice"]
    tts_provider: str = SettingsFields.model_fields["tts_provider"]
    tts_base_url: str = SettingsFields.model_fields["tts_base_url"]
    tts_api_key: str = Field(default="", max_length=2048, repr=False)
    tts_model: str = SettingsFields.model_fields["tts_model"]
    tts_voice: str = SettingsFields.model_fields["tts_voice"]
    asr_provider: str = SettingsFields.model_fields["asr_provider"]
    windows_asr_language: str = SettingsFields.model_fields["windows_asr_language"]
    asr_base_url: str = SettingsFields.model_fields["asr_base_url"]
    asr_api_key: str = Field(default="", max_length=2048, repr=False)
    asr_model: str = SettingsFields.model_fields["asr_model"]
    gpt_sovits_ref_audio_path: str = SettingsFields.model_fields["gpt_sovits_ref_audio_path"]
    gpt_sovits_prompt_text: str = SettingsFields.model_fields["gpt_sovits_prompt_text"]
    gpt_sovits_prompt_lang: str = SettingsFields.model_fields["gpt_sovits_prompt_lang"]
    gpt_sovits_text_lang: str = SettingsFields.model_fields["gpt_sovits_text_lang"]

    @model_validator(mode="after")
    def validate_skins(self):
        if not self.skins:
            self.skins = [CharacterSkin(id="default", name="默认服饰", model_id=self.model_id)]
            self.active_skin_id = "default"
        if len({skin.id for skin in self.skins}) != len(self.skins):
            raise ValueError("皮肤标识不能重复。")
        active = next((skin for skin in self.skins if skin.id == self.active_skin_id), None)
        if active is None:
            raise ValueError("当前皮肤必须在角色卡的皮肤列表中。")
        self.model_id = active.model_id
        return self

    @field_validator("id")
    @classmethod
    def validate_id(cls, value: str) -> str:
        value = value.lower()
        if value in {"con", "prn", "aux", "nul", *(f"com{i}" for i in range(1, 10)), *(f"lpt{i}" for i in range(1, 10))}:
            raise ValueError("角色标识不能使用 Windows 保留名称。")
        return value

    @field_validator("name", "persona")
    @classmethod
    def validate_identity(cls, value: str) -> str:
        return SettingsFields.validate_identity(value)

    @field_validator("tts_provider")
    @classmethod
    def validate_provider(cls, value: str) -> str:
        if value not in {"edge", "openai", "gpt_sovits"}:
            raise ValueError("语音服务必须为 edge、openai 或 gpt_sovits。")
        return value

    @field_validator("asr_provider")
    @classmethod
    def validate_asr_provider(cls, value: str) -> str:
        if value not in {"openai", "windows"}:
            raise ValueError("语音识别服务必须为 openai 或 windows。")
        return value

    @field_validator("tts_base_url", "asr_base_url")
    @classmethod
    def validate_url(cls, value: str) -> str:
        return SettingsFields.validate_base_url(value)

    @field_validator("actions")
    @classmethod
    def validate_actions(cls, value: list[CharacterAction]) -> list[CharacterAction]:
        return SettingsFields.validate_actions(value)

    @classmethod
    def from_settings(cls, value: RuntimeSettings) -> CharacterCard:
        data = value.model_dump()
        return cls(id=value.active_character_id, name=value.pet_name, persona=value.pet_persona,
                   **{field: data[field] for field in CARD_SETTING_FIELDS})

    def apply(self, value: RuntimeSettings) -> RuntimeSettings:
        data = value.model_dump()
        card = self.model_dump()
        data.update({field: card[field] for field in CARD_SETTING_FIELDS})
        data.update(active_character_id=self.id, pet_name=self.name, pet_persona=self.persona)
        return RuntimeSettings.model_validate(data)

    def public(self) -> dict:
        data = self.model_dump(mode="json")
        for field in CARD_CREDENTIAL_FIELDS:
            data[field + "_configured"] = bool(data[field].strip())
            data[field] = ""
        return data


class CharacterWriteRequest(BaseModel):
    """Partial writes, including blank-preserves/explicit-clear credential semantics."""
    id: str | None = Field(default=None, pattern=CHARACTER_ID_PATTERN)
    name: str | None = None
    persona: str | None = None
    model_id: str | None = None
    skins: list[CharacterSkin] | None = Field(default=None, min_length=1, max_length=32)
    active_skin_id: str | None = Field(default=None, pattern=CHARACTER_ID_PATTERN)
    allow_ai_skin_switch: bool | None = None
    actions: list[CharacterAction] | None = None
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
    clear_tts_api_key: bool = False
    clear_asr_api_key: bool = False

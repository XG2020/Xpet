from __future__ import annotations

import json
import os
import sys
import tempfile
import logging
from pathlib import Path
from threading import RLock
from pydantic_settings import BaseSettings, SettingsConfigDict
from protected_credentials import (
    CREDENTIAL_FIELDS, CredentialProtectionError, protect_credentials, unprotect_credentials,
)
from schemas import PublicRuntimeSettings, RuntimeSettings, RuntimeSettingsUpdate
from characters import CharacterCard, CharacterWriteRequest, CARD_CREDENTIAL_FIELDS
from errors import ServiceError
from uuid import uuid4
from resource_libraries import library_root, export_character_actions

# Frozen apps keep writable settings beside the executable, outside _internal.
BACKEND_DIR = Path(sys.executable).resolve().parent if getattr(sys, "frozen", False) else Path(__file__).resolve().parent


class BootstrapSettings(BaseSettings):
    model_config = SettingsConfigDict(env_file=BACKEND_DIR / ".env", env_file_encoding="utf-8", extra="ignore")
    app_name: str = "desktop-pet-backend"
    host: str = "127.0.0.1"
    port: int = 18080
    runtime_settings_file: str = "runtime_settings.json"
    pet_name: str = "Lita"
    pet_persona: str = RuntimeSettings.model_fields["pet_persona"].default
    llm_provider: str = "openai"
    llm_model: str = "gpt-4.1-mini"
    openai_api_key: str = ""
    openai_base_url: str = ""
    enable_real_rpa: bool = False
    save_conversation_history: bool = False
    enable_long_term_memory: bool = False
    enable_proactive_screen: bool = False
    proactive_screen_interval_seconds: int = 90
    allowed_apps: str = "notepad,calc,explorer,msedge"
    default_tts_voice: str = "zh-CN-XiaoxiaoNeural"
    tts_provider: str = "edge"
    tts_base_url: str = ""
    tts_api_key: str = ""
    tts_model: str = "gpt-4o-mini-tts"
    tts_voice: str = "alloy"
    asr_provider: str = "openai"
    windows_asr_language: str = "zh-CN"
    asr_base_url: str = ""
    asr_api_key: str = ""
    asr_model: str = "whisper-1"
    gpt_sovits_ref_audio_path: str = ""
    gpt_sovits_prompt_text: str = ""
    gpt_sovits_prompt_lang: str = "zh"
    gpt_sovits_text_lang: str = "zh"

    def to_runtime_defaults(self) -> RuntimeSettings:
        return RuntimeSettings(**{
            key: getattr(self, key) for key in RuntimeSettings.model_fields if key != "allowed_apps" and hasattr(self, key)
        }, allowed_apps=[item.strip() for item in self.allowed_apps.split(",") if item.strip()])


class SettingsManager:
    def __init__(self, bootstrap: BootstrapSettings) -> None:
        self.bootstrap = bootstrap
        self.path = BACKEND_DIR / bootstrap.runtime_settings_file
        self.library_dir = library_root(BACKEND_DIR) if self.path.parent == BACKEND_DIR else self.path.parent.resolve()
        self._lock = RLock()
        self._cards: dict[str, CharacterCard] = {}
        self._settings = self._load()
        if not self._cards:
            self._cards[self._settings.active_character_id] = CharacterCard.from_settings(self._settings)
        self._settings = self._cards[self._settings.active_character_id].apply(self._settings)

    def _load(self) -> RuntimeSettings:
        defaults = self.bootstrap.to_runtime_defaults()
        if not self.path.exists():
            return defaults
        try:
            data = json.loads(self.path.read_text(encoding="utf-8-sig"))
            if not isinstance(data, dict):
                raise ValueError
            cards = data.pop("character_cards", None)
            if cards is not None:
                if not isinstance(cards, list) or not 1 <= len(cards) <= 100:
                    raise ValueError
                for card_data in cards:
                    if not isinstance(card_data, dict):
                        raise ValueError
                    card_data = dict(card_data)
                    if "protected_credentials" in card_data:
                        if any(card_data.get(key, "") for key in CARD_CREDENTIAL_FIELDS):
                            raise CredentialProtectionError("角色卡同时含有明文和受保护的密钥。")
                        decoded = unprotect_credentials(card_data.pop("protected_credentials"))
                        card_data.update({key: decoded[key] for key in CARD_CREDENTIAL_FIELDS})
                    card = CharacterCard.model_validate(card_data)
                    if card.id in self._cards:
                        raise ValueError
                    self._cards[card.id] = card
            if "protected_credentials" in data:
                # A protected file is authoritative, including deliberately cleared keys.
                # Mixed plaintext/ciphertext files are rejected instead of losing a value.
                if any(data.get(key, "") != "" for key in CREDENTIAL_FIELDS):
                    raise CredentialProtectionError("设置文件同时含有明文和受保护的密钥。")
                data.update(unprotect_credentials(data.pop("protected_credentials")))
            merged = defaults.model_dump()
            merged.update(data)
            value = RuntimeSettings.model_validate(merged)
            if cards is not None:
                if "default" not in self._cards or value.active_character_id not in self._cards:
                    raise ValueError
                value = self._cards[value.active_character_id].apply(value)
            return value
        except CredentialProtectionError as error:
            raise RuntimeError(f"无法读取设置文件 {self.path.name}。{error} 原文件未改动。") from None
        except Exception:
            raise RuntimeError(f"无法读取设置文件 {self.path.name}。请修复该文件，原文件未改动。") from None

    def _save(self, value: RuntimeSettings, cards: dict[str, CharacterCard] | None = None) -> None:
        data = value.model_dump(mode="json")
        # Finish all encryption before opening a temporary file or replacing the original.
        # Legacy plaintext settings migrate only when a normal settings update succeeds.
        protected = protect_credentials({key: data[key] for key in CREDENTIAL_FIELDS})
        for key in CREDENTIAL_FIELDS:
            data[key] = ""
        data["protected_credentials"] = protected
        cards = cards if cards is not None else self._cards
        data["character_cards"] = []
        for card in cards.values():
            record = card.model_dump(mode="json")
            record["protected_credentials"] = protect_credentials({key: record[key] for key in CARD_CREDENTIAL_FIELDS})
            for key in CARD_CREDENTIAL_FIELDS:
                record[key] = ""
            data["character_cards"].append(record)
        serialized = json.dumps(data, ensure_ascii=False, indent=2)
        self.path.parent.mkdir(parents=True, exist_ok=True)
        fd, temporary = tempfile.mkstemp(prefix=".settings-", suffix=".tmp", dir=self.path.parent)
        try:
            with os.fdopen(fd, "w", encoding="utf-8") as file:
                file.write(serialized)
                file.flush()
                os.fsync(file.fileno())
            os.replace(temporary, self.path)
        finally:
            if os.path.exists(temporary):
                os.unlink(temporary)
        # Settings remain authoritative. Derived files contain action fields only.
        try:
            export_character_actions(self.library_dir, cards.values())
        except (OSError, ValueError):
            logging.getLogger(__name__).warning("动作库导出暂时失败，角色设置已经保存。")

    def get(self) -> RuntimeSettings:
        with self._lock:
            return self._settings.model_copy(deep=True)

    def public(self) -> PublicRuntimeSettings:
        value = self.get()
        data = value.model_dump()
        for key in CREDENTIAL_FIELDS:
            data[key + "_configured"] = bool(data[key].strip())
            data[key] = ""
        return PublicRuntimeSettings(**data)

    def update(self, payload: RuntimeSettingsUpdate) -> PublicRuntimeSettings:
        with self._lock:
            if payload.active_character_id and payload.active_character_id != self._settings.active_character_id:
                raise ServiceError("角色已切换，请重新加载当前设置。", 409)
            keys = CREDENTIAL_FIELDS
            fields = payload.model_dump(exclude_unset=True, exclude_none=True, exclude={"clear_" + key for key in keys})
            for key in keys:
                if not (fields.get(key) or "").strip():
                    fields.pop(key, None)
                elif key in fields:
                    fields[key] = fields[key].strip()
                if getattr(payload, "clear_" + key):
                    fields[key] = ""
            merged = self._settings.model_dump()
            merged.update(fields)
            if "model_id" in fields:
                for skin in merged["skins"]:
                    if skin["id"] == merged["active_skin_id"]:
                        skin["model_id"] = fields["model_id"]
            updated = RuntimeSettings.model_validate(merged)
            cards = dict(self._cards)
            cards[updated.active_character_id] = CharacterCard.from_settings(updated)
            self._save(updated, cards)
            self._settings = updated
            self._cards = cards
            return self.public()

    def list_characters(self) -> dict:
        with self._lock:
            return {"active_character_id": self._settings.active_character_id,
                    "cards": [card.public() for card in self._cards.values()]}

    def credential_values(self) -> tuple[str, ...]:
        """For in-process log redaction only; never expose through an endpoint."""
        with self._lock:
            return tuple(getattr(self._settings, key) for key in CREDENTIAL_FIELDS) + tuple(
                getattr(card, key) for card in self._cards.values() for key in CARD_CREDENTIAL_FIELDS)

    def character(self, character_id: str) -> CharacterCard:
        with self._lock:
            if character_id not in self._cards:
                raise ServiceError("找不到这张角色卡。", 404)
            return self._cards[character_id].model_copy(deep=True)

    def for_character(self, character_id: str | None = None) -> RuntimeSettings:
        with self._lock:
            return self.character(character_id or self._settings.active_character_id).apply(self._settings)

    def write_character(self, payload: CharacterWriteRequest, character_id: str | None = None) -> dict:
        with self._lock:
            creating = character_id is None
            if creating:
                if len(self._cards) >= 100:
                    raise ServiceError("最多保存 100 张角色卡。", 409)
                character_id = payload.id.lower() if payload.id else "character_" + uuid4().hex
                if character_id in self._cards:
                    raise ServiceError("角色标识已存在。", 409)
                base = CharacterCard(id=character_id, name="新角色", persona="你是桌面伙伴，说话自然、简洁，以第一人称回应。")
            else:
                base = self.character(character_id)
            fields = payload.model_dump(exclude_unset=True, exclude_none=True, exclude={"id", "clear_tts_api_key", "clear_asr_api_key"})
            for key in CARD_CREDENTIAL_FIELDS:
                if not (fields.get(key) or "").strip():
                    fields.pop(key, None)
                elif key in fields:
                    fields[key] = fields[key].strip()
                if getattr(payload, "clear_" + key):
                    fields[key] = ""
            record = base.model_dump()
            record.update(fields)
            # Legacy model selection edits the current outfit; explicit skin lists
            # are authoritative and derive the compatibility model_id themselves.
            if "model_id" in fields and "skins" not in fields:
                for skin in record["skins"]:
                    if skin["id"] == record["active_skin_id"]:
                        skin["model_id"] = fields["model_id"]
            card = CharacterCard.model_validate(record)
            cards = dict(self._cards)
            cards[card.id] = card
            updated = card.apply(self._settings) if card.id == self._settings.active_character_id else self._settings
            self._save(updated, cards)
            self._settings, self._cards = updated, cards
            return {"card": card.public()}

    def activate_character(self, character_id: str) -> dict:
        with self._lock:
            card = self.character(character_id)
            updated = card.apply(self._settings)
            self._save(updated)
            self._settings = updated
            return {"card": card.public(), "settings": self.public().model_dump(mode="json")}

    def activate_skin(self, character_id: str, skin_id: str) -> dict:
        with self._lock:
            if character_id != self._settings.active_character_id:
                raise ServiceError("角色已切换，请重新加载后再换装。", 409)
            card = self.character(character_id)
            if not any(skin.id == skin_id for skin in card.skins):
                raise ServiceError("这套皮肤不在当前角色卡中。", 404)
            record = card.model_dump()
            record["active_skin_id"] = skin_id
            card = CharacterCard.model_validate(record)
            updated = card.apply(self._settings)
            cards = dict(self._cards)
            cards[character_id] = card
            self._save(updated, cards)
            self._settings, self._cards = updated, cards
            return {"card": card.public(), "settings": self.public().model_dump(mode="json")}

    def delete_character(self, character_id: str) -> dict:
        with self._lock:
            self.character(character_id)
            if character_id == "default" or character_id == self._settings.active_character_id:
                raise ServiceError("默认角色或当前使用的角色不能删除，请先切换角色。", 409)
            cards = dict(self._cards)
            del cards[character_id]
            self._save(self._settings, cards)
            self._cards = cards
            return {"ok": True, "id": character_id}


def bootstrap_runtime() -> BootstrapSettings:
    """An explicit QA data directory starts from defaults without reading .env."""
    isolated = os.environ.get("DESKTOP_PET_ISOLATED_DATA_DIR", "").strip()
    if not isolated:
        return BootstrapSettings()
    directory = Path(isolated).resolve()
    if directory == BACKEND_DIR:
        raise RuntimeError("隔离测试数据目录不能使用正式后端目录。")
    defaults = {key: field.get_default() for key, field in BootstrapSettings.model_fields.items()}
    defaults.update(app_name="desktop-pet-backend-qa", host="127.0.0.1", port=18081,
                    runtime_settings_file=str(directory / "qa-runtime-settings.json"), llm_provider="offline",
                    openai_api_key="", tts_api_key="", asr_api_key="")
    return BootstrapSettings(_env_file=None, **defaults)


settings = bootstrap_runtime()
settings_manager = SettingsManager(settings)
DATA_DIR = settings_manager.path.parent if os.environ.get("DESKTOP_PET_ISOLATED_DATA_DIR") else BACKEND_DIR
LIBRARY_DIR = settings_manager.library_dir

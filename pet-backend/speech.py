from __future__ import annotations

import asyncio
import io
import re
import wave
from urllib.parse import urlsplit
import httpx
import ai_agent
import windows_asr
from config import settings_manager
from ai_agent import PetAgent
from errors import ServiceError

MAX_AUDIO_BYTES = 10 * 1024 * 1024


def audio_media_type(audio: bytes) -> str:
    if len(audio) >= 12 and audio[:4] == b"RIFF" and audio[8:12] == b"WAVE":
        return "audio/wav"
    if audio[:3] == b"ID3" or (len(audio) > 1 and audio[0] == 255 and audio[1] & 224 == 224 and audio[1] & 6 != 0):
        return "audio/mpeg"
    raise ServiceError("语音服务没有返回 MP3 或 WAV 音频，请检查服务的输出格式。", 502)


def speech_is_configured(value, kind: str) -> bool:
    if kind == "asr" and value.asr_provider == "windows":
        return windows_asr.recognizer_status(value.windows_asr_language)["windows_language_available"]
    if kind == "tts" and value.tts_provider == "edge":
        import importlib.util
        return importlib.util.find_spec("edge_tts") is not None
    if kind == "tts" and value.tts_provider == "gpt_sovits":
        return bool(value.gpt_sovits_ref_audio_path.strip())
    base, key = getattr(value, kind + "_base_url"), getattr(value, kind + "_api_key")
    if not base and not key:
        return PetAgent().is_configured(value)
    return bool(key) or urlsplit(base).hostname in {"localhost", "127.0.0.1", "::1"}


def build_speech_client(value, kind: str, agent: PetAgent):
    base_url = getattr(value, kind + "_base_url").strip()
    api_key = getattr(value, kind + "_api_key").strip()
    if not base_url and not api_key:
        return agent.build_client(value)
    # Never forward a chat credential to an independently configured speech server.
    base_url = base_url or value.openai_base_url or (
        "http://127.0.0.1:11434/v1" if value.llm_provider == "ollama" else "https://api.openai.com/v1"
    )
    local = urlsplit(base_url).hostname in {"localhost", "127.0.0.1", "::1"}
    if not api_key and not local:
        raise ServiceError("请为独立语音接口填写 API Key；不会把聊天密钥自动发送到该地址。", 409)
    if ai_agent.AsyncOpenAI is None:
        raise ServiceError("缺少 openai 依赖，请运行后端安装脚本。", 503)
    return ai_agent.AsyncOpenAI(base_url=base_url, api_key=api_key or "local",
                               timeout=60.0, max_retries=1)


async def _edge_speech(text: str, voice: str) -> bytes:
    if not re.fullmatch(r"[A-Za-z]{2,3}-[A-Za-z]{2,4}-[A-Za-z0-9]+Neural", voice):
        raise ServiceError("Edge 音色格式无效，例如 zh-CN-XiaoxiaoNeural。", 422)
    try:
        import edge_tts
    except ImportError:
        raise ServiceError("缺少 edge-tts 依赖，请运行后端安装脚本。", 503) from None
    async with asyncio.timeout(40):
        audio = bytearray()
        async for chunk in edge_tts.Communicate(text, voice).stream():
            if chunk["type"] == "audio":
                audio.extend(chunk["data"])
                if len(audio) > MAX_AUDIO_BYTES:
                    raise ServiceError("生成的语音过长，请缩短朗读内容。", 413)
    return bytes(audio)


async def _sovits_speech(text: str, value) -> bytes:
    if not value.gpt_sovits_ref_audio_path.strip():
        raise ServiceError("请设置 GPT-SoVITS 参考音频路径（语音服务器能够访问的路径）。", 409)
    base = value.tts_base_url or "http://127.0.0.1:9880"
    endpoint = base.rstrip("/") if base.rstrip("/").endswith("/tts") else base.rstrip("/") + "/tts"
    payload = {
        "text": text, "text_lang": value.gpt_sovits_text_lang,
        "ref_audio_path": value.gpt_sovits_ref_audio_path,
        "prompt_text": value.gpt_sovits_prompt_text, "prompt_lang": value.gpt_sovits_prompt_lang,
        "media_type": "wav", "streaming_mode": False,
    }
    headers = {"Authorization": "Bearer " + value.tts_api_key} if value.tts_api_key else {}
    async with httpx.AsyncClient(timeout=60, follow_redirects=False) as client:
        async with client.stream("POST", endpoint, json=payload, headers=headers) as response:
            if response.status_code != 200:
                raise ServiceError(f"GPT-SoVITS 返回 HTTP {response.status_code}，请检查 api_v2 服务、参考音频及语言配置。", 502)
            data = bytearray()
            async for chunk in response.aiter_bytes():
                data.extend(chunk)
                if len(data) > MAX_AUDIO_BYTES:
                    raise ServiceError("生成的语音过长，请缩短朗读内容。", 413)
    return bytes(data)


async def synthesize(text: str, voice: str | None = None, value=None) -> bytes:
    if not text.strip():
        raise ServiceError("朗读文本不能为空。", 422)
    value = value or settings_manager.get()
    try:
        if value.tts_provider == "edge":
            audio = await _edge_speech(text, voice or value.default_tts_voice)
        elif value.tts_provider == "gpt_sovits":
            audio = await _sovits_speech(text, value)
        else:
            async with build_speech_client(value, "tts", PetAgent()) as client:
                result = await client.audio.speech.create(model=value.tts_model, voice=voice or value.tts_voice,
                                                         input=text, response_format="mp3")
                audio = result.content
        if not audio:
            raise ServiceError("语音服务未返回音频，请稍后再试。", 502)
        if len(audio) > MAX_AUDIO_BYTES:
            raise ServiceError("生成的语音过长，请缩短朗读内容。", 413)
        audio_media_type(audio)
        return audio
    except ServiceError:
        raise
    except (TimeoutError, httpx.TimeoutException):
        raise ServiceError("语音服务响应超时，请检查网络后重试。", 504) from None
    except httpx.RequestError:
        raise ServiceError("无法连接语音服务，请检查接口地址及网络。", 502) from None
    except Exception as error:
        if value.tts_provider == "openai":
            raise PetAgent.external_error(error) from None
        raise ServiceError("语音服务暂不可用，请检查网络、服务状态或音色。", 502) from None


async def transcribe(audio: bytes, agent: PetAgent, value=None) -> str:
    try:
        with wave.open(io.BytesIO(audio), "rb") as source:
            if source.getnchannels() not in {1, 2} or source.getsampwidth() not in {1, 2, 3, 4}:
                raise ValueError("unsupported PCM")
            seconds = source.getnframes() / source.getframerate()
            if seconds <= 0 or seconds > 120:
                raise ValueError("invalid duration")
    except (wave.Error, EOFError, ValueError, ZeroDivisionError):
        raise ServiceError("请上传 120 秒以内的 PCM WAV 录音。", 422) from None
    value = value or settings_manager.get()
    try:
        if value.asr_provider == "windows":
            text = await asyncio.to_thread(windows_asr.transcribe, audio, value.windows_asr_language)
        else:
            async with build_speech_client(value, "asr", agent) as client:
                result = await client.audio.transcriptions.create(model=value.asr_model, file=("recording.wav", audio, "audio/wav"))
            text = result.text or ""
        text = text.strip()
        if not text:
            raise ServiceError("未识别到语音，请检查麦克风后重试。", 422)
        return text[:10000]
    except ServiceError:
        raise
    except Exception as error:
        if value.asr_provider == "windows":
            raise ServiceError("Windows 本地语音识别失败，请检查已安装的识别引擎和录音格式。", 503) from None
        raise agent.external_error(error) from None


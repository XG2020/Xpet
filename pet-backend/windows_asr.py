"""Offline Windows SAPI recognition of uploaded PCM; never opens a microphone."""
from __future__ import annotations

import io
import locale
import sys
from threading import Lock
import time
import wave

from errors import ServiceError

RECOGNIZER_CATEGORY = r"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Speech\Recognizers"
RECO_CONTEXT_EVENTS = "{7B8FCB42-0E9D-4F00-A048-7B04D6179D3D}"
_recognition_lock = Lock()


def _sapi_modules():
    if sys.platform != "win32":
        raise ServiceError("Windows 本地语音识别只能在 Windows 上使用。", 503)
    try:
        import pythoncom
        from win32com.client import VARIANT
        from win32com.client.dynamic import Dispatch
        from win32com.client.connect import SimpleConnection
    except ImportError:
        raise ServiceError("缺少 Windows 本地识别依赖 pywin32，请运行后端安装脚本。", 503) from None
    return pythoncom, Dispatch, VARIANT, SimpleConnection


def _recognizers(dispatch) -> list[dict]:
    # Enumerating a token category creates no recognizer or audio device.
    category = dispatch("SAPI.SpObjectTokenCategory")
    category.SetId(RECOGNIZER_CATEGORY, False)
    tokens = category.EnumerateTokens()
    result = []
    for index in range(tokens.Count):
        token = tokens.Item(index)
        languages = []
        for item in token.GetAttribute("Language").split(";"):
            try:
                language = locale.windows_locale.get(int(item.strip(), 16), "")
            except ValueError:
                continue
            if language:
                languages.append(language.replace("_", "-"))
        result.append({"id": token.Id, "name": token.GetDescription(),
                       "languages": list(dict.fromkeys(languages))})
    return result


def _engine_error(error: Exception) -> ServiceError:
    hresult = getattr(error, "hresult", 0)
    exception_info = getattr(error, "excepinfo", None)
    if hresult & 0xFFFFFFFF == 0x80020009 and exception_info and exception_info[5]:
        hresult = exception_info[5]
    if hresult & 0xFFFFFFFF == 0x80070005:
        return ServiceError("Windows 本地识别引擎访问被系统拒绝，请检查离线语音组件和当前程序权限；不会自动转到云端识别。", 503)
    if hresult & 0xFFFFFFFF == 0x80040154:
        return ServiceError("当前程序无法加载已登记的 Windows SAPI 识别引擎，请检查 Windows 离线语音组件是否完整；不会自动转到云端识别。", 503)
    return ServiceError("Windows 本地语音识别无法运行，请检查已安装的 SAPI 识别引擎及 PCM WAV 格式；不会自动转到云端识别。", 503)


def recognizer_status(language: str = "zh-CN") -> dict:
    status = {"windows_available": False, "windows_language_available": False,
              "recognizers": [], "windows_error": ""}
    initialized = False
    try:
        pythoncom, dispatch, _, _ = _sapi_modules()
        pythoncom.CoInitialize()
        initialized = True
        engines = _recognizers(dispatch)
        status["recognizers"] = engines
        status["windows_available"] = bool(engines)
        status["windows_language_available"] = any(
            language.casefold() in {item.casefold() for item in engine["languages"]} for engine in engines)
        if not engines:
            status["windows_error"] = "未安装可用的 Windows SAPI 本地识别引擎。"
        elif not status["windows_language_available"]:
            status["windows_error"] = f"未安装支持 {language} 的 Windows SAPI 本地识别引擎；不会自动转到云端识别。"
    except ServiceError as error:
        status["windows_error"] = error.message
    except Exception as error:
        status["windows_error"] = _engine_error(error).message
    finally:
        if initialized:
            pythoncom.CoUninitialize()
    return status


class _RecognitionEvents:
    """Explicit connection-point sink avoids writing a generated COM cache."""
    _public_methods_ = ["OnRecognition", "OnEndStream"]
    _dispid_to_func_ = {7: "OnRecognition", 2: "OnEndStream"}

    def _query_interface_(self, iid):
        if str(iid).upper() == RECO_CONTEXT_EVENTS:
            from win32com.server.util import wrap
            return wrap(self)
        return None

    def __init__(self, dispatch):
        self.dispatch = dispatch
        self.parts: list[str] = []
        self.done = False
        self.failed = False

    def OnRecognition(self, _stream, _position, _kind, result):
        try:
            text = self.dispatch(result).PhraseInfo.GetText().strip()
            if text:
                self.parts.append(text)
        except Exception:
            self.failed = True

    def OnEndStream(self, _stream, _position, _released):
        self.done = True


def _pcm_audio(audio: bytes):
    try:
        with wave.open(io.BytesIO(audio), "rb") as source:
            channels, width, rate, frames = (source.getnchannels(), source.getsampwidth(),
                                             source.getframerate(), source.getnframes())
            seconds = frames / rate
            if channels not in {1, 2} or width not in {1, 2, 3, 4} or not 0 < seconds <= 120:
                raise ValueError
            pcm = source.readframes(frames)
            if len(pcm) != frames * channels * width:
                raise ValueError
        return pcm, channels, width, rate, seconds
    except (wave.Error, EOFError, ValueError, ZeroDivisionError):
        raise ServiceError("请上传 120 秒以内、内容完整的 PCM WAV 录音。", 422) from None


def transcribe(audio: bytes, language: str = "zh-CN") -> str:
    pcm, channels, width, rate, seconds = _pcm_audio(audio)
    pythoncom, dispatch, variant, connection_class = _sapi_modules()
    if not _recognition_lock.acquire(timeout=5):
        raise ServiceError("Windows 本地语音识别正在处理另一段录音，请稍后重试。", 409)
    initialized = False
    failure = None
    recognizer = context = grammar = stream = token = connection = audio_format = wave_format = None
    try:
        pythoncom.CoInitialize()
        initialized = True
        engines = _recognizers(dispatch)
        engine = next((item for item in engines if language.casefold() in {
            value.casefold() for value in item["languages"]}), None)
        if engine is None:
            raise ServiceError(f"未安装支持 {language} 的 Windows SAPI 本地识别引擎；不会自动转到云端识别。", 409)

        audio_format = dispatch("SAPI.SpAudioFormat")
        audio_format.Type = 18  # Initialize PCM fields before specifying the uploaded format.
        wave_format = audio_format.GetWaveFormatEx()
        wave_format.FormatTag = 1
        wave_format.Channels = channels
        wave_format.SamplesPerSec = rate
        wave_format.BitsPerSample = width * 8
        wave_format.BlockAlign = channels * width
        wave_format.AvgBytesPerSec = rate * channels * width
        audio_format.SetWaveFormatEx(wave_format)
        stream = dispatch("SAPI.SpMemoryStream")
        stream.Format = audio_format
        stream.SetData(variant(pythoncom.VT_ARRAY | pythoncom.VT_UI1, pcm))
        # SpInprocRecognizer has no shared/default microphone. Install the memory
        # stream before making a recognition context or activating any grammar.
        recognizer = dispatch("SAPI.SpInprocRecognizer")
        recognizer.State = 0
        token = dispatch("SAPI.SpObjectToken")
        token.SetId(engine["id"])
        recognizer.Recognizer = token
        recognizer.AudioInputStream = stream
        context = recognizer.CreateRecoContext()
        context.EventInterests = 1 | 16  # EndStream and Recognition only.
        events = _RecognitionEvents(dispatch)
        connection = connection_class(context, events, RECO_CONTEXT_EVENTS)
        grammar = context.CreateGrammar(1)
        grammar.DictationLoad("", 0)
        grammar.DictationSetState(1)
        recognizer.State = 1
        deadline = time.monotonic() + max(15.0, seconds + 10.0)
        while not events.done:
            if time.monotonic() >= deadline:
                raise ServiceError("Windows 本地语音识别超时，请缩短录音后重试。", 504)
            if pythoncom.PumpWaitingMessages():
                raise ServiceError("Windows 本地语音识别已中断，请重试。", 503)
            time.sleep(0.01)
        if events.failed:
            raise ServiceError("Windows 本地识别引擎没有返回有效结果，请重试。", 503)
        text = " ".join(events.parts).strip()
        if not text:
            raise ServiceError("未识别到语音，请检查录音后重试。", 422)
        return text[:10000]
    except ServiceError as error:
        failure = error
    except Exception as error:
        # Raising here would retain the COM exception's traceback as __context__,
        # including references to proxies after CoUninitialize. Release it first.
        failure = _engine_error(error)
    finally:
        if recognizer is not None:
            try:
                recognizer.State = 3  # InactiveWithPurge: discard buffered input.
            except Exception:
                pass
        if grammar is not None:
            try:
                grammar.DictationSetState(0)
            except Exception:
                pass
        if connection is not None:
            try:
                connection.Disconnect()
            except Exception:
                pass
        # All COM references must be released on this worker before uninitializing.
        recognizer = context = grammar = stream = token = connection = audio_format = wave_format = None
        if initialized:
            pythoncom.CoUninitialize()
        _recognition_lock.release()
    raise failure from None

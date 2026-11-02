from __future__ import annotations

import asyncio
import base64
import importlib.util
import json
import math
import time
from collections import OrderedDict
from contextlib import asynccontextmanager
from typing import Any
from uuid import uuid4
from fastapi import FastAPI, Query, Request, WebSocket, WebSocketDisconnect
from fastapi.exceptions import RequestValidationError
from fastapi.middleware.cors import CORSMiddleware
from fastapi.responses import JSONResponse, Response, StreamingResponse
from starlette.datastructures import Headers
from starlette.middleware.trustedhost import TrustedHostMiddleware
from pydantic import ValidationError
from ai_agent import PetAgent
from config import BACKEND_DIR, DATA_DIR, settings, settings_manager
from resource_libraries import migrate_resource_libraries
from characters import CharacterWriteRequest
from diagnostics import diagnostic_log
from conversation_store import ConversationStore
from errors import ServiceError
from memory_store import MemoryStore, explicit_memory
from workspace_memory import WorkspaceMemoryStore
from workspace_memory_api import build_workspace_memory_router
from model_library import router as model_router
from skill_book import router as skill_book_router
from dynamic_actions import router as action_library_router, ActionBroker
from protected_credentials import CredentialProtectionError
from os_controller import OSController
from perception import capture_screen, read_selected_file
from schemas import BackendEvent, ChatRequest, ChatResponse, ExecuteActionRequest, FileAnalyzeRequest, MemoryWriteRequest, ProactiveScreenRequest, ProactiveScreenResponse, PublicRuntimeSettings, RuntimeSettingsUpdate, SpeechRequest
from speech import synthesize, transcribe, audio_media_type, speech_is_configured
import windows_asr

state: dict[str, Any] = {}


@asynccontextmanager
async def lifespan(_: FastAPI):
    migrate_resource_libraries(BACKEND_DIR)
    state["agent"] = PetAgent()
    state["os_controller"] = OSController()
    state["conversation_store"] = ConversationStore(DATA_DIR / "conversations.sqlite3")
    state["memory_store"] = MemoryStore(DATA_DIR / "memories.sqlite3")
    state["proactive_screen"] = ProactiveScreenCoordinator()
    diagnostic_log.add("INFO", "backend", "后端已启动。")
    yield
    state["proactive_screen"].invalidate()
    diagnostic_log.add("INFO", "backend", "后端已停止。")
    state.clear()


APP_VERSION = "0.6.0"
app = FastAPI(title=settings.app_name, version=APP_VERSION, lifespan=lifespan)
app.include_router(model_router)
app.include_router(skill_book_router)
app.include_router(action_library_router)
app.add_middleware(TrustedHostMiddleware, allowed_hosts=["localhost", "127.0.0.1", "[::1]"])
allowed_origins = [f"http://127.0.0.1:{settings.port}", f"http://localhost:{settings.port}"]
app.add_middleware(CORSMiddleware, allow_origins=allowed_origins, allow_methods=["GET", "PUT", "POST", "DELETE"], allow_headers=["Content-Type"])


def local_origin_allowed(origin: str | None) -> bool:
    return not origin or origin in allowed_origins


class LocalRequestGuard:
    """Pure ASGI middleware keeps disconnect messages visible to SSE consumers."""

    def __init__(self, app):
        self.app = app

    async def __call__(self, scope, receive, send):
        if scope["type"] == "http":
            headers = Headers(scope=scope)
            error = None
            if not local_origin_allowed(headers.get("origin")):
                error = JSONResponse({"detail": "不允许来自外部网页的请求。"}, status_code=403)
            else:
                try:
                    length = int(headers.get("content-length", "0"))
                    if length < 0:
                        raise ValueError
                    if length > 9 * 1024 * 1024:
                        error = JSONResponse({"detail": "请求内容过大。"}, status_code=413)
                except ValueError:
                    error = JSONResponse({"detail": "无效的请求长度。"}, status_code=400)
            if error is not None:
                await error(scope, receive, send)
                return
        if scope["type"] != "http":
            await self.app(scope, receive, send)
            return
        started = time.monotonic()
        status = 500
        async def tracked_send(message):
            nonlocal status
            if message["type"] == "http.response.start":
                status = message["status"]
            await send(message)
        try:
            await self.app(scope, receive, tracked_send)
        finally:
            route = scope.get("route")
            # Use the matched route template, never a user-supplied URL/query string.
            path = getattr(route, "path", "未匹配路由")
            if path not in {"/api/logs", "/health"}:
                level = "ERROR" if status >= 500 else "WARNING" if status >= 400 else "INFO"
                diagnostic_log.add(level, "http", f"{scope.get('method', 'HTTP')} {path} → {status}，{round((time.monotonic() - started) * 1000)} ms")


app.add_middleware(LocalRequestGuard)


@app.exception_handler(ServiceError)
async def service_error_handler(_: Request, error: ServiceError):
    diagnostic_log.add("ERROR" if error.status_code >= 500 else "WARNING", "service", error.message,
                       settings_manager.credential_values())
    return JSONResponse({"detail": error.message}, status_code=error.status_code)


@app.exception_handler(CredentialProtectionError)
async def credential_error_handler(_: Request, error: CredentialProtectionError):
    return JSONResponse({"detail": str(error)}, status_code=500)


@app.exception_handler(OSError)
async def storage_error_handler(_: Request, error: OSError):
    diagnostic_log.add("ERROR", "storage", "无法读写本地文件，请检查目录权限和可用空间。")
    return JSONResponse({"detail": "无法读写本地文件，请检查目录权限和可用空间。"}, status_code=500)


def validation_message(error: ValidationError | RequestValidationError) -> str:
    # Never echo pydantic's input values: settings requests can contain credentials.
    return "；".join(".".join(map(str, item["loc"])) + ": " + item["msg"] for item in error.errors())[:1000]


@app.exception_handler(RequestValidationError)
async def validation_error_handler(_: Request, error: RequestValidationError):
    return JSONResponse({"detail": validation_message(error)}, status_code=422)


@app.exception_handler(ValidationError)
async def settings_error_handler(_: Request, error: ValidationError):
    return JSONResponse({"detail": validation_message(error)}, status_code=422)


@app.get("/health")
async def health() -> dict[str, Any]:
    value = settings_manager.get()
    configured = state["agent"].is_configured(value)
    return {
        "ok": True, "app": settings.app_name, "version": APP_VERSION, "pet_name": value.pet_name,
        "llm_model": value.llm_model, "real_rpa_enabled": value.enable_real_rpa,
        "mode": "online" if configured else "offline", "llm_configured": configured,
        "capabilities": {"tts": speech_is_configured(value, "tts"),
                         "screen_capture": importlib.util.find_spec("mss") is not None,
                         "asr": speech_is_configured(value, "asr"), "file_analysis": configured},
    }


@app.get("/api/settings", response_model=PublicRuntimeSettings)
async def get_runtime_settings() -> PublicRuntimeSettings:
    return settings_manager.public()


@app.put("/api/settings", response_model=PublicRuntimeSettings)
async def update_runtime_settings(payload: RuntimeSettingsUpdate) -> PublicRuntimeSettings:
    try:
        result = settings_manager.update(payload)
        if "proactive_screen" in state and any(name in payload.model_fields_set for name in
                ("enable_proactive_screen", "llm_provider", "llm_model", "openai_base_url", "openai_api_key", "clear_openai_api_key")):
            state["proactive_screen"].invalidate()
        return result
    except OSError:
        raise ServiceError("无法保存设置，请检查后端目录的写入权限。", 500) from None


@app.post("/api/llm/test")
async def test_llm_connection() -> dict[str, Any]:
    value = settings_manager.get()
    agent = state["agent"]
    if not agent.is_configured(value):
        raise ServiceError("请先选择 OpenAI 兼容或 Ollama 服务，并保存模型与密钥；离线体验不能测试模型连接。", 409)
    started = time.monotonic()
    try:
        # Use the actual chat path, including JSON and tool compatibility. This
        # endpoint never publishes actions or writes conversations and memories.
        async with asyncio.timeout(100):
            response = await agent.generate_reply("这是连接测试，请只简短回复你好，不需要动作、读取技能、换装或操作电脑。", value=value)
    except TimeoutError:
        raise ServiceError("设置已保存，但模型连接测试超时。请稍后重试或检查服务负载。", 504) from None
    current = settings_manager.get()
    fields = ("active_character_id", "llm_provider", "llm_model", "openai_base_url", "openai_api_key")
    if any(getattr(current, field) != getattr(value, field) for field in fields):
        raise ServiceError("模型配置或角色已改变，请重新测试当前连接。", 409)
    if response.mode != "online" or not response.text.strip():
        raise ServiceError("接口没有返回有效的在线回复，请检查模型配置。", 502)
    return {"ok": True, "model": value.llm_model, "elapsed_seconds": round(time.monotonic() - started, 2),
            "message": "连接成功，模型已返回有效回复。"}


@app.get("/api/characters")
async def list_characters():
    return settings_manager.list_characters()


@app.get("/api/characters/{character_id}")
async def get_character(character_id: str):
    return {"card": settings_manager.character(character_id).public()}


@app.post("/api/characters")
async def create_character(payload: CharacterWriteRequest):
    return settings_manager.write_character(payload)


@app.put("/api/characters/{character_id}")
async def update_character(character_id: str, payload: CharacterWriteRequest):
    result = settings_manager.write_character(payload, character_id)
    if character_id == settings_manager.get().active_character_id:
        state["proactive_screen"].invalidate()
    return result


@app.post("/api/characters/{character_id}/activate")
async def activate_character(character_id: str):
    result = settings_manager.activate_character(character_id)
    state["proactive_screen"].invalidate()
    return result


@app.delete("/api/characters/{character_id}")
async def delete_character(character_id: str):
    return settings_manager.delete_character(character_id)


@app.post("/api/characters/{character_id}/skins/{skin_id}/activate")
async def activate_skin(character_id: str, skin_id: str):
    return settings_manager.activate_skin(character_id, skin_id)


@app.get("/api/logs")
async def get_logs(after_id: int = Query(default=0, ge=0), limit: int = Query(default=200, ge=1, le=500),
                   level: str = Query(default="", pattern=r"^(?i:|info|warning|error)$")):
    return diagnostic_log.read(after_id, limit, level)


@app.delete("/api/logs")
async def clear_logs():
    diagnostic_log.clear()
    return {"ok": True}


def character_store(kind: str, character_id: str | None = None):
    character_id = character_id or settings_manager.get().active_character_id
    original = state[kind + "_store"]
    if character_id == "default":
        return original
    directory = original.path.parent / "characters" / character_id
    return MemoryStore(directory / "memories.sqlite3") if kind == "memory" else ConversationStore(directory / "conversations.sqlite3")


def current_character_settings(character_id: str | None = None):
    value = settings_manager.get()
    if character_id and character_id != value.active_character_id:
        raise ServiceError("角色已切换，请使用当前角色重新发起请求。", 409)
    return value


def workspace_memory_store(character_id: str) -> WorkspaceMemoryStore:
    return WorkspaceMemoryStore(state["memory_store"].path.parent / "workspace-memory", character_id)


app.include_router(build_workspace_memory_router(
    lambda character_id: current_character_settings(character_id).active_character_id, workspace_memory_store,
))


class ProactiveScreenCoordinator:
    """Single active observation and bounded, memory-only idempotency results."""
    def __init__(self):
        self.epoch = 0
        self.last_capture_at = -math.inf
        self.active_task: asyncio.Task | None = None
        self.active_request_id: str | None = None
        self.results: OrderedDict[str, ProactiveScreenResponse] = OrderedDict()

    def invalidate(self):
        self.epoch += 1
        self.results.clear()
        if self.active_task is not None and not self.active_task.done():
            self.active_task.cancel()

    async def observe(self, payload: ProactiveScreenRequest, connection: Request) -> ProactiveScreenResponse:
        value = settings_manager.get()
        if not value.enable_proactive_screen:
            raise ServiceError("主动看屏幕已关闭。", 409)
        agent = state["agent"]
        if not agent.is_configured(value):
            raise ServiceError("请先配置支持图片输入的模型与接口，再开启主动看屏幕。", 409)
        request_id = payload.request_id
        if request_id in self.results:
            return self.results[request_id].model_copy(deep=True)
        if self.active_task is not None:
            return ProactiveScreenResponse(request_id=request_id, status="busy", retry_after_seconds=5)
        remaining = value.proactive_screen_interval_seconds - (time.monotonic() - self.last_capture_at)
        if remaining > 0:
            return ProactiveScreenResponse(request_id=request_id, status="rate_limited", retry_after_seconds=math.ceil(remaining))
        epoch = self.epoch

        def permitted():
            return self.epoch == epoch and settings_manager.get().enable_proactive_screen

        async def produce():
            if not permitted() or await connection.is_disconnected():
                return ProactiveScreenResponse(request_id=request_id, status="discarded")
            # Capture uses mss's primary monitor and encodes in memory, without a file path.
            image_url = await asyncio.to_thread(capture_screen)
            if not permitted() or await connection.is_disconnected():
                return ProactiveScreenResponse(request_id=request_id, status="discarded")
            response = await agent.generate_proactive_reply(image_url, value)
            return response.model_copy(update={"request_id": request_id})

        async def interrupted():
            while permitted() and not await connection.is_disconnected():
                await asyncio.sleep(0.05)

        # No await between reservation and creation: concurrent callers cannot also capture.
        self.last_capture_at = time.monotonic()
        self.active_request_id = request_id
        task = self.active_task = asyncio.create_task(produce())
        watcher = asyncio.create_task(interrupted())
        try:
            done, _ = await asyncio.wait({task, watcher}, return_when=asyncio.FIRST_COMPLETED)
            if watcher in done or not permitted() or await connection.is_disconnected() or task.cancelled():
                return ProactiveScreenResponse(request_id=request_id, status="discarded")
            response = task.result()
            self.results[request_id] = response.model_copy(deep=True)
            while len(self.results) > 32:
                self.results.popitem(last=False)
            return response
        finally:
            for pending in (task, watcher):
                if not pending.done():
                    pending.cancel()
            await asyncio.gather(task, watcher, return_exceptions=True)
            if self.active_task is task:
                self.active_task = None
                self.active_request_id = None


@app.post("/api/proactive/screen", response_model=ProactiveScreenResponse)
async def proactive_screen(payload: ProactiveScreenRequest, request: Request, response: Response) -> ProactiveScreenResponse:
    response.headers["Cache-Control"] = "no-store"
    return await state["proactive_screen"].observe(payload, request)


@app.post("/api/chat", response_model=ChatResponse)
async def chat(request: ChatRequest) -> ChatResponse:
    request = identify_chat(request)
    history, image_url = await chat_context(request)
    memories = await chat_memories(request)
    workspace_memories = await chat_workspace_memories(request)
    response = await state["agent"].generate_reply(request.message, history, image_url=image_url, memories=memories,
                                                  interaction_zone=request.interaction_zone, affinity=request.affinity, value=request._runtime,
                                                  workspace_memories=workspace_memories)
    response = identify_response(request, response)
    save_completed_chat(request, response)
    return response


def identify_chat(request: ChatRequest) -> ChatRequest:
    value = current_character_settings(request.character_id)
    result = request.model_copy(update={"request_id": request.request_id or uuid4().hex,
                                      "session_id": request.session_id or uuid4().hex,
                                      "character_id": value.active_character_id})
    result._runtime = value
    return result


def identify_response(request: ChatRequest, response: ChatResponse) -> ChatResponse:
    value = request._runtime
    current = settings_manager.get()
    if value is not None and response.action_id:
        response = ActionBroker(value, settings_manager.get, interaction_zone=request.interaction_zone, affinity=request.affinity).finish(response)
    allowed = value is not None and value.allow_ai_skin_switch and current.allow_ai_skin_switch
    allowed = allowed and current.active_character_id == request.character_id
    ids = {skin.id for skin in value.skins} & {skin.id for skin in current.skins} if allowed else set()
    skin_id = response.skin_id if response.skin_id in ids else ""
    return response.model_copy(update={"request_id": request.request_id, "session_id": request.session_id,
                                       "character_id": request.character_id, "skin_id": skin_id})


async def chat_context(request: ChatRequest) -> tuple[list[dict[str, str]], str | None]:
    agent: PetAgent = state["agent"]
    history = request.conversation_history
    # Explicit history (including []) is authoritative; never append saved copies.
    if "conversation_history" not in request.model_fields_set:
        history = await asyncio.to_thread(character_store("conversation", request.character_id).get_messages, request.session_id, 20, True) or []
    image_url = None
    if request.include_screen_context:
        if not agent.is_configured(request._runtime):
            raise ServiceError("看屏幕需要先在设置页配置支持视觉的模型与接口。", 409)
        image_url = await asyncio.to_thread(capture_screen)
    return history, image_url


def save_completed_chat(request: ChatRequest, response: ChatResponse) -> None:
    # Check again at completion: switching persistence off during a reply takes effect.
    if request._runtime.save_conversation_history and settings_manager.get().save_conversation_history:
        character_store("conversation", request.character_id).save_pair(request.session_id, request.request_id, request.message, response.text, request.interaction_zone)
    if request._runtime.enable_long_term_memory and settings_manager.get().enable_long_term_memory and request.interaction_zone is None:
        candidate = explicit_memory(request.message)
        if candidate:
            character_store("memory", request.character_id).save(**candidate, source="explicit_user", session_id=request.session_id, request_id=request.request_id)


async def chat_memories(request: ChatRequest) -> list[dict]:
    if not (request._runtime or settings_manager.get()).enable_long_term_memory:
        return []
    # Body interaction status is ephemeral, not a query about personal facts.
    if request.interaction_zone is not None:
        return []
    return await asyncio.to_thread(character_store("memory", request.character_id).search, request.message)


async def chat_workspace_memories(request: ChatRequest) -> list[dict]:
    value = request._runtime or settings_manager.get()
    if not value.enable_long_term_memory or request.interaction_zone is not None:
        return []
    return await asyncio.to_thread(workspace_memory_store(value.active_character_id).context_records, request.message)


async def _chat_stream_items(request: ChatRequest):
    history, image_url = await chat_context(request)
    memories = await chat_memories(request)
    workspace_memories = await chat_workspace_memories(request)
    stream = state["agent"].stream_reply(request.message, history, image_url=image_url, memories=memories,
                                         interaction_zone=request.interaction_zone, affinity=request.affinity, value=request._runtime,
                                         workspace_memories=workspace_memories)
    try:
        async for item in stream:
            yield item
    finally:
        await stream.aclose()


async def chat_stream_events(payload: ChatRequest, connection: Request):
    """Cancel the in-flight SDK read when the browser disconnects, even while idle."""
    request = payload if payload._runtime is not None else identify_chat(payload)
    request_id = request.request_id
    yield {"type": "start", "request_id": request_id, "session_id": request.session_id, "character_id": request.character_id}

    async def disconnected():
        while not await connection.is_disconnected():
            await asyncio.sleep(0.05)

    watcher = asyncio.create_task(disconnected())
    items = _chat_stream_items(request)
    pending = None
    try:
        while True:
            pending = asyncio.create_task(anext(items))
            done, _ = await asyncio.wait({pending, watcher}, return_when=asyncio.FIRST_COMPLETED)
            if watcher in done or await connection.is_disconnected():
                return
            try:
                item = pending.result()
            except StopAsyncIteration:
                raise ServiceError("模型流在完成回复前结束，请重试。", 502) from None
            if isinstance(item, ChatResponse):
                response = identify_response(request, item)
                save_completed_chat(request, response)
                yield {"type": "final", "request_id": request_id, "response": response.model_dump()}
                return
            if item:
                yield {"type": "delta", "request_id": request_id, "text": item, "character_id": request.character_id}
    except ServiceError as error:
        if not await connection.is_disconnected():
            yield {"type": "error", "request_id": request_id, "message": error.message}
    except Exception:
        if not await connection.is_disconnected():
            yield {"type": "error", "request_id": request_id, "message": "流式对话暂时不可用，请稍后重试。"}
    finally:
        tasks = [watcher] + ([pending] if pending is not None else [])
        for task in tasks:
            if not task.done():
                task.cancel()
        await asyncio.gather(*tasks, return_exceptions=True)
        await items.aclose()


@app.post("/api/chat/stream")
async def chat_stream(payload: ChatRequest, request: Request) -> StreamingResponse:
    payload = identify_chat(payload)
    async def encoded_events():
        events = chat_stream_events(payload, request)
        try:
            async for event in events:
                yield "data: " + json.dumps(event, ensure_ascii=False) + "\n\n"
        finally:
            await events.aclose()

    return StreamingResponse(encoded_events(), media_type="text/event-stream",
                             headers={"Cache-Control": "no-cache, no-store", "X-Accel-Buffering": "no"})


@app.get("/api/conversations")
async def list_conversations(character_id: str | None = None) -> dict[str, Any]:
    character_id = current_character_settings(character_id).active_character_id
    return {"character_id": character_id, "conversations": await asyncio.to_thread(character_store("conversation", character_id).list_conversations)}


@app.get("/api/conversations/{session_id}")
async def get_conversation(session_id: str, character_id: str | None = None) -> dict[str, Any]:
    character_id = current_character_settings(character_id).active_character_id
    messages = await asyncio.to_thread(character_store("conversation", character_id).get_messages, session_id)
    if messages is None:
        raise ServiceError("找不到这段已保存的会话。", 404)
    return {"character_id": character_id, "session_id": session_id, "messages": messages}


@app.delete("/api/conversations/{session_id}")
async def delete_conversation(session_id: str, character_id: str | None = None) -> dict[str, Any]:
    character_id = current_character_settings(character_id).active_character_id
    if not await asyncio.to_thread(character_store("conversation", character_id).delete_conversation, session_id):
        raise ServiceError("找不到这段已保存的会话。", 404)
    return {"ok": True, "character_id": character_id, "session_id": session_id}


@app.post("/api/files/analyze", response_model=ChatResponse)
async def analyze_file(request: FileAnalyzeRequest) -> ChatResponse:
    value = current_character_settings(request.character_id)
    agent: PetAgent = state["agent"]
    if not agent.is_configured(value):
        raise ServiceError("文件分析需要先在设置页配置 AI 模型与接口。", 409)
    image_url, file_context = await asyncio.to_thread(read_selected_file, request.path)
    response = await agent.generate_reply(request.message, image_url=image_url, file_context=file_context, value=value)
    return response.model_copy(update={"character_id": value.active_character_id})


@app.get("/api/memories")
async def list_memories(character_id: str | None = None) -> dict[str, Any]:
    character_id = current_character_settings(character_id).active_character_id
    return {"character_id": character_id, "memories": await asyncio.to_thread(character_store("memory", character_id).list_memories)}


def memory_fields(payload: MemoryWriteRequest, value=None):
    fields = payload.model_dump()
    if payload.perspective == "self":
        # Only explicitly self-labelled memories are rewritten; user facts and old
        # records keep their exact meaning. A card rename needs no data rewriting.
        name = (value or settings_manager.get()).pet_name
        fields["content"] = fields["content"].replace(name + "的", "我的").replace(name, "我")
        fields["key"] = fields["key"].replace(name + "的", "我的").replace(name, "我")
    return fields


@app.post("/api/memories")
async def add_memory(payload: MemoryWriteRequest, character_id: str | None = None) -> dict[str, Any]:
    value = current_character_settings(character_id)
    memory = await asyncio.to_thread(character_store("memory", value.active_character_id).save, **memory_fields(payload, value), source="manual")
    return {"character_id": value.active_character_id, "memory": memory}


@app.put("/api/memories/{memory_id}")
async def update_memory(memory_id: int, payload: MemoryWriteRequest, character_id: str | None = None) -> dict[str, Any]:
    value = current_character_settings(character_id)
    memory = await asyncio.to_thread(character_store("memory", value.active_character_id).save, **memory_fields(payload, value), source="manual", memory_id=memory_id)
    return {"character_id": value.active_character_id, "memory": memory}


@app.delete("/api/memories/{memory_id}")
async def delete_memory(memory_id: int, character_id: str | None = None) -> dict[str, Any]:
    character_id = current_character_settings(character_id).active_character_id
    if not await asyncio.to_thread(character_store("memory", character_id).delete, memory_id):
        raise ServiceError("找不到这条长期记忆。", 404)
    return {"ok": True, "character_id": character_id, "id": memory_id}


@app.post("/api/tts")
async def text_to_speech(request: SpeechRequest) -> Response:
    audio = await synthesize(request.text, request.voice, value=current_character_settings(request.character_id))
    return Response(audio, media_type=audio_media_type(audio), headers={"Cache-Control": "no-store"})


@app.post("/api/asr")
async def speech_to_text(request: Request) -> dict[str, str]:
    value = current_character_settings(request.query_params.get("character_id"))
    if request.headers.get("content-type", "").split(";")[0].lower() not in {"audio/wav", "audio/x-wav", "application/octet-stream"}:
        raise ServiceError("请将 WAV 音频作为请求正文发送，Content-Type: audio/wav。", 415)
    audio = bytearray()
    async for chunk in request.stream():
        audio.extend(chunk)
        if len(audio) > 8 * 1024 * 1024:
            raise ServiceError("录音不能超过 8 MB。", 413)
    text = await transcribe(bytes(audio), state["agent"], value=value)
    return {"text": text, "character_id": value.active_character_id}


@app.get("/api/speech/recognizers")
async def speech_recognizers(character_id: str | None = None) -> dict[str, Any]:
    value = current_character_settings(character_id)
    result = await asyncio.to_thread(windows_asr.recognizer_status, value.windows_asr_language)
    return {"asr_provider": value.asr_provider, "windows_asr_language": value.windows_asr_language, **result}


@app.post("/api/actions/execute")
async def execute_action(request: ExecuteActionRequest) -> dict[str, Any]:
    command = request.command
    if command.type == "screen_analyze":
        response = await chat(ChatRequest(message=command.target or "请看看当前屏幕。", include_screen_context=True))
        return {"ok": True, "executed": True, "message": response.text, "response": response.model_dump()}
    if command.type == "tts_speak":
        text = command.args.get("text") or command.target or ""
        payload = SpeechRequest(text=text)
        audio = await synthesize(payload.text)
        return {"ok": True, "executed": True, "message": "语音已生成。", "audio_base64": base64.b64encode(audio).decode("ascii"), "media_type": audio_media_type(audio)}
    return await state["os_controller"].execute(command)


@app.websocket("/ws")
async def websocket_endpoint(websocket: WebSocket) -> None:
    if not local_origin_allowed(websocket.headers.get("origin")):
        await websocket.close(code=1008)
        return
    await websocket.accept()
    await websocket.send_text(BackendEvent(type="connected", payload={"message": "桌面宠物后端已连接。"}).model_dump_json())
    try:
        while True:
            raw = await websocket.receive_text()
            if len(raw) > 65536:
                await websocket.send_json({"type": "error", "payload": {"message": "消息过长。"}})
                continue
            try:
                try:
                    data = json.loads(raw)
                except json.JSONDecodeError:
                    data = {"type": "chat", "message": raw}
                if not isinstance(data, dict):
                    raise ServiceError("WebSocket 消息必须是 JSON 对象。", 422)
                message_type = data.get("type", "chat")
                if message_type == "ping":
                    event = BackendEvent(type="pong")
                elif message_type == "chat":
                    response = await chat(ChatRequest.model_validate(data))
                    event = BackendEvent(type="chat_response", payload=response.model_dump())
                elif message_type == "execute":
                    result = await execute_action(ExecuteActionRequest.model_validate(data))
                    event = BackendEvent(type="action_result", payload=result)
                elif message_type == "get_settings":
                    event = BackendEvent(type="settings", payload=settings_manager.public().model_dump())
                elif message_type == "update_settings":
                    result = await update_runtime_settings(RuntimeSettingsUpdate.model_validate(data.get("payload", {})))
                    event = BackendEvent(type="settings", payload=result.model_dump())
                else:
                    raise ServiceError("不支持的 WebSocket 消息类型。", 422)
            except (ValidationError, RequestValidationError) as error:
                event = BackendEvent(type="error", payload={"message": validation_message(error)})
            except ServiceError as error:
                event = BackendEvent(type="error", payload={"message": error.message, "status": error.status_code})
            await websocket.send_text(event.model_dump_json())
    except WebSocketDisconnect:
        pass


if __name__ == "__main__":
    import uvicorn
    uvicorn.run(app, host=settings.host, port=settings.port, reload=False)

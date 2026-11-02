"""Anthropic Messages/SystemOne adapter for the existing bounded tool session."""
from __future__ import annotations
import asyncio
import json
import re
from types import SimpleNamespace as Obj
import httpx
from openai import APIConnectionError, APITimeoutError, APIStatusError, AuthenticationError, RateLimitError
from errors import ServiceError


def messages_url(base: str) -> str:
    base = base.strip().rstrip("/")
    if base.endswith("/messages"): return base
    return base + ("/messages" if base.endswith("/v1") else "/v1/messages")


def content_blocks(content):
    if isinstance(content, str): return [{"type": "text", "text": content}] if content else []
    blocks = []
    for item in content or []:
        if item.get("type") == "text":
            blocks.append({"type": "text", "text": item.get("text", "")})
        elif item.get("type") == "image_url":
            url = item["image_url"]["url"]
            match = re.fullmatch(r"data:(image/(?:png|jpeg|webp|gif));base64,(.+)", url, re.DOTALL)
            source = {"type": "base64", "media_type": match[1], "data": match[2]} if match else {"type": "url", "url": url}
            blocks.append({"type": "image", "source": source})
    return blocks


def request_payload(arguments: dict) -> dict:
    systems, messages = [], []
    for message in arguments["messages"]:
        role = message["role"]
        if role == "system":
            systems.extend(content_blocks(message.get("content")))
            continue
        if role == "tool":
            role = "user"
            blocks = [{"type": "tool_result", "tool_use_id": message["tool_call_id"], "content": message.get("content") or ""}]
        else:
            blocks = content_blocks(message.get("content"))
            for call in message.get("tool_calls") or []:
                try: inputs = json.loads(call["function"]["arguments"] or "{}")
                except (ValueError, TypeError): raise ServiceError("工具参数不是有效 JSON，本轮已停止。", 502) from None
                blocks.append({"type": "tool_use", "id": call["id"], "name": call["function"]["name"], "input": inputs})
        if not blocks: continue
        if messages and messages[-1]["role"] == role: messages[-1]["content"].extend(blocks)
        else: messages.append({"role": role, "content": blocks})
    payload = {"model": arguments["model"], "messages": messages, "max_tokens": arguments.get("max_tokens", 2048)}
    if systems: payload["system"] = systems
    if arguments.get("stream"): payload["stream"] = True
    tools = arguments.get("tools") or []
    choice = arguments.get("tool_choice", "auto")
    # Final text rounds omit tools rather than sending OpenAI-specific options.
    if tools and choice != "none":
        payload["tools"] = [{"name": tool["function"]["name"], "description": tool["function"].get("description", ""),
                             "input_schema": tool["function"]["parameters"]} for tool in tools]
        payload["tool_choice"] = {"type": "auto", "disable_parallel_tool_use": arguments.get("parallel_tool_calls") is False}
        if isinstance(choice, dict) and choice.get("function"):
            payload["tool_choice"].update(type="tool", name=choice["function"]["name"])
    return payload


def status_error(response: httpx.Response, body):
    message = str(body.get("error", {}).get("message", "")) if isinstance(body, dict) and isinstance(body.get("error"), dict) else ""
    message = message or "Anthropic Messages request failed"
    error_type = AuthenticationError if response.status_code in {401, 403} else RateLimitError if response.status_code == 429 else APIStatusError
    return error_type(message, response=response, body=body)


class AnthropicStream:
    def __init__(self, response): self.response = response
    async def close(self): await self.response.aclose()
    def __aiter__(self): return self.iterate()
    async def iterate(self):
        try:
            async for line in self.response.aiter_lines():
                if not line.startswith("data:"): continue
                try: event = json.loads(line[5:].strip())
                except ValueError: raise ServiceError("Anthropic 流式响应包含无效数据。", 502) from None
                kind = event.get("type")
                delta = Obj(content=None, tool_calls=None)
                finish = None
                if kind == "error": raise ServiceError("Anthropic 流式接口返回错误，本轮未执行动作。", 502)
                if kind == "message_stop": break
                if kind == "content_block_start":
                    block = event.get("content_block", {})
                    if block.get("type") == "tool_use":
                        initial = block.get("input") or {}
                        delta.tool_calls = [Obj(index=event["index"], id=block["id"], function=Obj(name=block["name"], arguments=json.dumps(initial, ensure_ascii=False) if initial else ""))]
                    elif block.get("type") == "text": delta.content = block.get("text") or None
                elif kind == "content_block_delta":
                    change = event.get("delta", {})
                    if change.get("type") == "text_delta": delta.content = change.get("text") or None
                    elif change.get("type") == "input_json_delta":
                        delta.tool_calls = [Obj(index=event["index"], id=None, function=Obj(name=None, arguments=change.get("partial_json", "")))]
                elif kind == "message_delta":
                    reason = event.get("delta", {}).get("stop_reason")
                    finish = {"end_turn": "stop", "tool_use": "tool_calls", "max_tokens": "length"}.get(reason)
                if delta.content is not None or delta.tool_calls or finish:
                    yield Obj(choices=[Obj(delta=delta, finish_reason=finish)])
        except httpx.TimeoutException as error:
            raise APITimeoutError(request=error.request) from None
        except httpx.RequestError as error:
            raise APIConnectionError(request=error.request) from None


class AnthropicClient:
    def __init__(self, base_url, api_key, *, timeout=45, transport=None):
        self.url = messages_url(base_url)
        self.http = httpx.AsyncClient(timeout=timeout, transport=transport)
        self.headers = {"x-api-key": api_key, "Authorization": "Bearer " + api_key,
                        "anthropic-version": "2023-06-01", "Content-Type": "application/json"}
        self.chat = Obj(completions=self)
    async def __aenter__(self): return self
    async def __aexit__(self, *args): await self.http.aclose()
    async def create(self, **arguments):
        payload = request_payload(arguments)
        streaming = arguments.get("stream", False)
        for attempt in range(2):
            try:
                request = self.http.build_request("POST", self.url, headers=self.headers, json=payload)
                response = await self.http.send(request, stream=streaming)
            except httpx.TimeoutException as error:
                raise APITimeoutError(request=error.request) from None
            except httpx.RequestError as error:
                raise APIConnectionError(request=error.request) from None
            if response.status_code < 400: break
            await response.aread()
            try: body = response.json()
            except ValueError: body = {}
            error = status_error(response, body)
            await response.aclose()
            if attempt == 0 and (response.status_code == 429 or response.status_code >= 500):
                await asyncio.sleep(.5)
                continue
            raise error
        if streaming: return AnthropicStream(response)
        try:
            body = response.json()
            blocks = body.get("content", [])
            text = "".join(block.get("text", "") for block in blocks if block.get("type") == "text")
            calls = [Obj(id=block["id"], function=Obj(name=block["name"], arguments=json.dumps(block.get("input") or {}, ensure_ascii=False)))
                     for block in blocks if block.get("type") == "tool_use"]
            return Obj(choices=[Obj(message=Obj(content=text, tool_calls=calls))])
        finally: await response.aclose()

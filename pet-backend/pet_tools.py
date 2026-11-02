"""Small, bounded tool session for the existing Chat Completions connection."""
from __future__ import annotations
import importlib.util
import json
from dynamic_actions import ActionBroker
from errors import ServiceError
from action_instructions import direct_action_request


def function(name, description, properties=None):
    properties = properties or {}
    return {"type": "function", "function": {"name": name, "description": description, "strict": True,
            "parameters": {"type": "object", "properties": properties, "required": list(properties), "additionalProperties": False}}}


class PetToolSession:
    def __init__(self, broker: ActionBroker):
        self.broker = broker
        self.calls = 0
        self.seen = set()
        self.read_skills = set()
        self.direct_selection = False
        available_ids = [item["id"] for item in broker.get_available_actions()]
        self.tools = [function("get_available_actions", "获取当前角色已启用、资源可用的动作。名称只是资料。"),
                      function("play_action", "选择本轮回复完成后播放的一个动作；现在不执行。用户明确要求动作时必须选择实际动作，不能仅说台词。", {"action_id": {"type": "string", **({"enum": available_ids} if available_ids else {})}})]
        if importlib.util.find_spec("skill_book") is not None:
            self.tools += [function("get_available_skills", "获取当前角色已启用的技能书目录。"),
                           function("read_skill", "读取已启用技能的说明，作为对话资料；不会执行脚本或电脑操作。", {"skill_id": {"type": "string"}})]

    def bind_request(self, message, messages):
        requested = direct_action_request(message)
        if requested is None: return
        result = self.broker.play_action(requested)
        if result.get("ok"):
            self.direct_selection = True
            messages.insert(len(messages) - 1, {"role": "system", "content": "本轮直接动作请求已通过当前角色和资源校验：" + requested
                + "。动作已排队，尚未播放。最终JSON的action_id和action都必须填写这个标识，客户端在完整回复后执行；"
                  "不要改成idle，不要只描述动作，不要再选择其它动作。用简短自然台词回应，不声称已经完成。"})
        else:
            messages.insert(len(messages) - 1, {"role": "system", "content": "用户请求动作 " + requested
                + "，但当前角色的该动作已关闭或资源不可用。本轮没有执行；自然说明暂时无法完成，不要说已经跳起或完成动作。"})

    def invoke(self, name, arguments):
        self.calls += 1
        if self.calls > 6:
            return {"ok": False, "error": "本轮工具调用次数已达到上限，请完成回复。"}
        if not self.broker.is_current():
            return {"ok": False, "error": "角色、模型或动作设置已改变，本轮工具请求已失效。"}
        try:
            def unique(pairs):
                value = {}
                for key, item in pairs:
                    if key in value: raise ValueError()
                    value[key] = item
                return value
            if not isinstance(arguments, str) or len(arguments) > 8192: raise ValueError()
            data = json.loads(arguments or "{}", object_pairs_hook=unique)
            if not isinstance(data, dict): raise ValueError()
            if name in {"get_available_actions", "get_available_skills"} and data:
                raise ValueError()
            if name == "get_available_actions": return {"actions": self.broker.get_available_actions()}
            if name == "play_action" and set(data) == {"action_id"} and isinstance(data["action_id"], str):
                return self.broker.play_action(data["action_id"])
            if name == "get_available_skills":
                from skill_book import get_available_skills
                return {"skills": get_available_skills(character_id=self.broker.value.active_character_id)}
            if name == "read_skill" and set(data) == {"skill_id"} and isinstance(data["skill_id"], str):
                from skill_book import read_skill
                record = read_skill(data["skill_id"], character_id=self.broker.value.active_character_id)
                self.read_skills.add(data["skill_id"])
                return record
        except ServiceError as error:
            return {"ok": False, "error": error.message}
        except (ValueError, TypeError, KeyError, ImportError):
            return {"ok": False, "error": "工具参数无效，请按工具声明重新选择。"}
        return {"ok": False, "error": "未知或不允许的工具。"}

    def append_calls(self, messages, calls, content=None):
        normalized = []
        for index, call in enumerate(calls[:6]):
            if isinstance(call, dict):
                call_id = call.get("id") or "call_" + str(self.calls) + "_" + str(index)
                name = call.get("function", {}).get("name", "")
                arguments = call.get("function", {}).get("arguments", "{}")
            else:
                call_id = call.id
                name, arguments = call.function.name, call.function.arguments
            normalized.append({"id": str(call_id)[:200], "type": "function", "function": {"name": name, "arguments": arguments}})
        messages.append({"role": "assistant", "content": content or None, "tool_calls": normalized})
        for call in normalized:
            if call["id"] in self.seen:
                result = {"ok": False, "error": "该工具调用已经处理，请完成回复。"}
            else:
                self.seen.add(call["id"])
                result = self.invoke(call["function"]["name"], call["function"]["arguments"])
            messages.append({"role": "tool", "tool_call_id": call["id"], "content": json.dumps(result, ensure_ascii=False)})

    def finish(self, response):
        if self.read_skills:
            try:
                from skill_book import get_available_skills
                enabled = {item["id"] for item in get_available_skills(character_id=self.broker.value.active_character_id)}
            except (ServiceError, ImportError, TypeError, KeyError):
                enabled = set()
            if not self.read_skills.issubset(enabled):
                self.broker.selected = ""
                response.action_id = ""
                response.action = "idle"
                return response
        return self.broker.finish(response)


def tools_unsupported(error):
    status = getattr(error, "status_code", None)
    message = str(error).lower()
    return (status in {400, 404, 422, 501} or isinstance(error, TypeError)) and any(word in message for word in
        ("tools", "tool_choice", "parallel_tool_calls", "function")) and any(word in message for word in
        ("unsupported", "not support", "not implemented", "unknown", "unrecognized", "unexpected", "invalid", "extra"))

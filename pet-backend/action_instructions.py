"""Executable action vocabulary and conservative direct-command routing."""
from __future__ import annotations
import re

ACTION_GUIDE = {
    "idle": ("自然待机，结束当前普通动作。", ("恢复待机", "回到待机", "停止动作")),
    "wave": ("抬手挥手打招呼。", ("挥手", "挥挥手", "挥手一下", "挥个手")),
    "happy": ("开心的小动作。", ("做个开心动作",)),
    "think": ("思考姿态。", ("做个思考动作",)),
    "sad": ("难过的姿态。", ("做个难过动作",)),
    "surprised": ("惊讶的姿态。", ("做个惊讶动作",)),
    "speak": ("说话姿态，配合口型。", ()),
    "walk": ("在桌面走几步。", ("走几步", "走一下", "走走", "散步", "散散步")),
    "nod": ("轻轻点头。", ("点头", "点点头", "点个头", "点头一下")),
    "tilt_head": ("歪头倾听。", ("歪头", "歪一下头", "歪歪头")),
    "shy": ("含蓄害羞的姿态。", ("做个害羞动作",)),
    "stretch": ("伸懒腰放松。", ("伸懒腰", "伸个懒腰", "伸伸懒腰")),
    "cheer": ("举手庆祝或鼓劲。", ("庆祝一下", "欢呼一下", "做个庆祝动作")),
    "look_around": ("左右张望。", ("左右看看", "环顾一下", "四处看看")),
    "jump": ("真正跳起并落地，不能仅用台词描述跳跃。", ("跳一下", "跳一跳", "跳起来", "跳跃", "jump")),
    "run": ("在桌面小跑。", ("跑一下", "跑几步", "跑起来", "小跑一下")),
    "leg_retract": ("收腿避让。", ("收一下腿",)),
    "leg_shift": ("轻轻调整腿部重心。", ("轻移重心",)),
    "leg_sway": ("小幅摆腿。", ("摆一下腿", "摆摆腿")),
    "angry_stomp": ("带生气表情跺脚一次。", ("跺脚", "跺一下脚", "跺跺脚")),
}


def direct_action_request(message: str) -> str | None:
    """Only complete, short commands qualify; quoted/negative/contextual text does not."""
    text = re.sub(r"\s+", "", message.strip()).lower().strip("。！？!?~～，,.")
    prefixes = ("我想让你", "请你", "麻烦你", "能不能", "你可以", "你能", "帮我", "给我", "可以", "请", "能")
    for _ in range(3):
        prefix = next((prefix for prefix in prefixes if text.startswith(prefix)), None)
        if prefix is None: break
        text = text[len(prefix):]
    for suffix in ("好吗", "吗", "吧"):
        if text.endswith(suffix): text = text[:-len(suffix)]
    return next((identity for identity, (_, aliases) in ACTION_GUIDE.items() if text in aliases), None)


def describe_actions(available: list[dict]) -> list[dict]:
    return [{**item, "usage": ACTION_GUIDE.get(item["id"], ("已启用的自定义动作，使用该标识调用 play_action。", ()))[0],
             "examples": list(ACTION_GUIDE.get(item["id"], ("", ()))[1])} for item in available]

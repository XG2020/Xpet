# Xpet 桌面宠物后端

0.6.0 的 FastAPI 本地服务，默认监听 `http://127.0.0.1:18080`。配置由客户端设置页保存；无密钥时可使用明确标识的离线互动模式。提供 SSE 聊天、可选 SQLite 会话历史、显式长期记忆、语音服务适配、默认关闭的主动看屏接口，以及角色卡与皮肤、本地模型库和运行时导入队列。

## 启动与测试

使用便携发行版时，完整解压后双击 `Xpet.exe`，启动器自动管理客户端与后端。发行包包含 Python 运行时、服务依赖和模型解析组件，无需另外安装 Python、Blender 或 Unity。

下述步骤仅用于源码开发与测试：

```powershell
cd pet-backend          # 从源码仓库根目录进入
.\setup.ps1             # 首次安装/升级依赖，需要 Python 3.11+
.\start.ps1             # 启动本地服务，Ctrl+C 停止
.\.venv\Scripts\python.exe -m unittest discover -s tests -v
```

已安装依赖的工作区可直接运行 `start.ps1`。也可执行：

```powershell
.\.venv\Scripts\python.exe -m uvicorn app:app --host 127.0.0.1 --port 18080
```

后端面向同一台电脑上的客户端提供服务，默认只监听回环地址。

## 前端配置

`GET /api/settings` 读取设置，`PUT /api/settings` 保存部分或全部字段：

- `pet_name`、`pet_persona`：名字与性格。
- `llm_provider`：`openai`、`openai-compatible`、`anthropic`、`systemone`、`ollama`、`offline`（`mock` 兼容旧设置）。Anthropic/SystemOne 使用 Messages 协议，地址可为服务根地址或 `/v1`。
- `llm_model`、`openai_base_url`、`openai_api_key`：使用自己的模型与兼容接口；Base URL 通常需包含 `/v1`。
- `tts_provider`：`edge`（默认）、`openai`（兼容 OpenAI 的语音合成接口）或 `gpt_sovits`。
- `default_tts_voice`：Edge TTS 音色，默认 `zh-CN-XiaoxiaoNeural`。
- `tts_base_url`、`tts_api_key`：独立语音合成接口地址与密钥。
- `tts_model`、`tts_voice`：OpenAI 兼容语音合成模型与音色，默认 `gpt-4o-mini-tts`、`alloy`。
- `asr_base_url`、`asr_api_key`、`asr_model`：独立语音识别接口、密钥与模型（默认 `whisper-1`）。
- `asr_provider`：`openai` 或 `windows`；Windows SAPI 使用 `windows_asr_language` 指定本地识别语言（默认 `zh-CN`）。
- `gpt_sovits_ref_audio_path`、`gpt_sovits_prompt_text`：GPT-SoVITS 参考音频路径与对应文本。
- `gpt_sovits_prompt_lang`、`gpt_sovits_text_lang`：参考音频和合成文本的语言，默认均为 `zh`。
- `enable_real_rpa`：是否实际打开程序和网址，默认关闭。
- `allowed_apps`：只允许 `notepad`、`calc`、`explorer`、`msedge` 中的应用。
- `save_conversation_history`：是否保存后续完整会话轮次，默认关闭。
- `enable_long_term_memory`：是否在普通聊天中检索已保存记忆、并处理明确的“记住…”请求，默认关闭。
- `enable_proactive_screen`：是否允许客户端空闲时主动看主屏幕，默认关闭；在“工具”页开启并保存。
- `proactive_screen_interval_seconds`：主动看屏间隔，范围 30–900 秒，默认 90 秒。

三项密钥在 Windows 上使用 **当前用户 DPAPI** 加密。HTTP 与 WebSocket 均返回空的 `openai_api_key`、`tts_api_key`、`asr_api_key`，用对应的 `*_api_key_configured` 布尔值表示是否已保存。空白输入保留现有密钥；明确发送 `clear_openai_api_key: true`、`clear_tts_api_key: true` 或 `clear_asr_api_key: true` 才清除对应密钥。

凭据加密成功后才提交设置；加密或解密失败会返回明确错误，不回退到明文保存。在非 Windows 环境中保存非空凭据会失败。

客户端的九页设置为角色卡、外观、对话、语音、记忆、工具、动作管理、模型导入、后端日志。人物大小、颜色、朗读开关、低功耗、唤醒词、按住说话开关/组合键及面板位置由客户端管理；角色的模型、皮肤、动作和语音通过角色卡同步，主动看屏开关与间隔通过本接口保存。托盘、Ctrl+Alt+P、DWM 透明窗口、头顶气泡及聊天/设置/数据管理标题栏拖动也由客户端实现。右键菜单和各面板单独将自身位置限制在应用画布与当前显示器工作区内，不通过移动角色来容纳面板；Escape 可关闭设置。

聊天采用宠物左、用户右的气泡，名字在气泡外上方；多行输入区下方是语音、朗读、本次屏幕和发送工具栏。独立“模型导入”页提供角色模型选择、刷新、切换与导入，使用下述模型库接口，与聊天 AI 模型配置相互独立。

`ollama` 无需密钥，Base URL 留空使用 `http://127.0.0.1:11434/v1`，模型名须填写Ollama 已安装的模型。`offline` 不发聊天模型请求。其他聊天提供商没有密钥时，普通聊天为离线互动；视觉和文件分析返回可读的配置错误。独立配置的 TTS、ASR 可在聊天处于离线模式时使用。

OpenAI 兼容 TTS/ASR 的独立地址和密钥都留空时，沿用聊天接口与密钥。只填写独立密钥时，沿用聊天 Base URL（未设置时使用对应默认地址）。一旦填写独立地址，远程服务必须填写该语音接口自己的密钥，后端不会自动把聊天密钥发往该地址。回环地址 `localhost`、`127.0.0.1`、`::1` 的兼容服务允许不填密钥。GPT-SoVITS 使用自己的地址与可选密钥，不沿用聊天配置。

## HTTP 协议

普通 HTTP 接口错误返回非 2xx 状态码和 `{ "detail": "可读错误信息" }`。SSE 在响应头已发送后，通过 `error` 事件报告生成失败。接口认证失败、网络失败、无效模型响应不会被伪装成成功的离线回复。

### 健康状态

`GET /health` 返回 `ok`、`version`、`pet_name`、`mode`、`llm_configured` 与 `capabilities`。能力字段表示依赖或配置可用，不代表已经验证远端服务。

### 角色模型库与导入

模型库接口管理已注册的角色模型，支持导入 `.petmodel` 模型包，以及将 PMX/FBX 转换为采用 PETRAW1 格式的 `.petraw` 运行时资源。每个模型通过独立的 `id` 供角色卡引用。

| 接口 | 作用 |
| --- | --- |
| `GET /api/models` | 返回 `{ "models": [...] }`，条目含 `id`、`label`、`bundle_path`、`source_type`；仅列出已注册且文件存在的导入包，内置丽塔由客户端加入列表 |
| `POST /api/models/import` | 正文 `{ "path": "模型文件绝对路径" }`；接受 `.pmx`、`.fbx`、`.petmodel`，返回 HTTP 202 与导入任务 |
| `GET /api/models/jobs/{job_id}` | 查询任务状态；未知任务返回 404 |

任务返回 `job_id`、`model_id`、`label`、`status` 和 `error`。`status` 为 `queued`（等待）、`converting`（PMX/FBX 运行时解析）、`importing`（已有模型包复制）、`completed` 或 `failed`；接受任务不代表导入成功，后续失败由 `failed` 和可读的 `error` 报告。队列串行执行，最多 4 个活跃任务（包含正在执行与排队）；队列已满返回 429，同一文件已有活跃任务时返回原任务。任务状态保存在后端进程内存中，服务重启后应刷新模型列表查看已完成的模型。

PMX 2.0/2.1 由包内解析器读取，FBX 由包内原生解析组件读取；两者写入 `.petraw` 缓存，客户端据此创建网格、骨架和材质并重定向内置动作。整个流程不调用 Blender、Unity 编辑器或项目源码。原始模型和贴图保持不变，贴图按引用复制；需要保留模型文件旁的外部纹理及其相对目录。游戏专用 shader、复杂材质及全部 MMD 物理不会自动还原。

已有 `.petmodel` 通过复制注册直接导入，不需要 Unity 编辑器或 Blender。后端检查包头和文件路径，独立客户端加载时再验证包与角色结构；`completed` 表示导入流程完成，不保证任意外部包都能在当前客户端加载。客户端成功加载后才替换角色并保存选择，失败保留现模型；启动恢复上次选择，失败保留内置丽塔，并可随时切回内置模型。切换保留人物大小和桌面位置。

导入只接受绝对文件路径，不接受 UNC 网络路径。入口的单文件上限为 1 GiB，单张外部贴图上限为 64 MiB；解析器另限制顶点、索引、骨骼和形变数据数量。模型必须有可识别的人形骨骼，只有附件骨架的资源不能作为独立角色。当前适配不保证任意骨架或所有衣饰没有穿插；VMD 动作由客户端单独导入，支持范围见下文。

### 聊天、动作与情绪

```http
POST /api/chat
Content-Type: application/json

{"message":"你好","include_screen_context":false,"conversation_history":[]}
```

```json
{
  "text": "你好呀！",
  "action": "wave",
  "emotion": "happy",
  "command": {"type":"noop","target":null,"args":{}},
  "audio_text": "你好呀！",
  "mode": "offline"
}
```

`action` 共 **17 项**：idle / wave / happy / think / sad / surprised / speak / walk / nod / tilt_head / shy / stretch / cheer / look_around / guard / jump / run。`emotion`: neutral / happy / curious / thinking / sad / excited。客户端生成 **19 个动画状态**，另外两项是拖拽和 GuardLower 内部变体；动作管理对应 18 个内置条目，GuardLower 与 Guard 共用开关。`walk` 和 `run` 请求短距离桌面移动并在工作区边界停止，`jump` 请求预备、离地和缓冲落地；它们不提供任意桌面路径规划。

新增看板动作映射如下，参考短回应的互动节奏，使用项目自制骨骼动画：

| action | 表达 |
| --- | --- |
| `nod` | 点头 |
| `tilt_head` | 歪头 |
| `shy` | 害羞 |
| `stretch` | 伸懒腰 |
| `cheer` | 轻庆祝 |
| `look_around` | 环顾 |
| `guard` | 后退并用手遮挡，表达触摸边界 |
| `jump` | 原地跳跃并缓冲落地 |
| `run` | 短距离跑步 |

请求支持 `request_id` 与 `session_id`，省略时由服务端生成，响应会携带相同标识。历史记录最多 40 条，每条至少包含 `{ "role": "user"或"assistant", "content": "..." }`，单条最多 8,000 字符；实际模型上下文限制为最近 20 条。显式传入的 `conversation_history`（包括空数组）优先；只有省略该字段时，后端才尝试读取此 `session_id` 的已保存上下文，不叠加两份历史。

分区触摸可传 `interaction_zone`（head / shoulder / hand / arm / chest / waist / lower_body / leg）与 `affinity`（0–100）。部位与熟悉度供本轮回复使用，触摸请求不检索或保存长期记忆。台词按三档好感度随机选择，采用简短口语回应；后端参考近期上下文避免重复，模型提示要求不复述“点击了哪里”、部位参数或好感度数值。前端忙碌/失败兜底独立可用，并避免同部位相邻两次使用同一句。

`affinity < 30` 时，`chest`（胸前）、`waist`（腰侧）和 `lower_body`（下腹）强制返回 `guard` 动作和 `neutral` 情绪；未提供好感度时按 20 判断。模型提示和离线台词要求明确叫停，客户端后退遮挡且不增加好感度。前后端均约束保护动作，不让朗读时的通用说话动作覆盖它。所有带触摸元数据的回复都清除电脑操作建议，返回 `noop`。

客户端给自动提交附加明确的交互元数据，把触摸提示隐藏于聊天展示，同时仍发给模型并显示助手回复；用户手动输入同一句话不会因文本相同而被隐藏。前端好感度与本地兜底台词不依赖后端持续运行。

`include_screen_context: true` 仅在本次聊天请求截取主屏幕，不保存截图。需要配置支持图片输入与 JSON 输出的模型，对应客户端下一条手动发送的“本次屏幕”（本次附带屏幕）。主动看屏使用下述独立接口与开关。

### SSE 流式聊天

`POST /api/chat/stream` 接受与 `/api/chat` 相同的请求，返回 `text/event-stream`。每帧是 `data: <JSON>`，以空行分隔：

| 事件 | 字段与用途 |
| --- | --- |
| `start` | `request_id`、`session_id`，标识本轮请求 |
| `delta` | `request_id`、`text`，追加可见回复片段 |
| `final` | `request_id`、`response`，完整 `ChatResponse`，包括最终文本、动作和情绪 |
| `error` | `request_id`、`message`，可读的失败原因 |

Unity 按请求 ID 接收文本片段，在 `final` 后应用最终动作、情绪与可选朗读。关闭朗读时逐步展示 `delta`；开启朗读时缓冲文字，完整回复合成后等 AudioSource 实际开始播放才显示，等待期间在聊天/头顶气泡显示三个动态圆点。口型由播放输出的 RMS 音量驱动；合成或播放失败时展示文字并报告朗读不可用。当前 TTS 是完整回复后合成，不是分句音频流。

客户端停止会中止连接；服务端检测断开并取消在途模型读取。未收到完整结果的片段不会作为完成对话入库，界面可保留“本轮未完成”的文字。客户端取消会丢弃尚未展示的回复和待播音频；若服务端已完成并保存了文本轮次，取消朗读不会撤销该条已保存记录。

### 可选主动看屏

`POST /api/proactive/screen`，JSON `{ "request_id": "本次唯一标识" }`。标识为 1–128 个字母、数字、下划线或连字符。默认关闭；必须保存 `enable_proactive_screen: true` 并配置支持图片与 JSON 输出的当前聊天模型。没有独立视觉模型配置。关闭或未配置时返回 409，不伪造离线视觉结果。

成功响应包含 `request_id`、`status`、`text`、`action`、`emotion` 和 `retry_after_seconds`，不含电脑操作 `command`：

| status | 含义 |
| --- | --- |
| `speak` | 一句简短回应，由客户端展示并按朗读开关处理 |
| `silent` | 保持安静，文本为空 |
| `busy` | 已有观察请求在处理，稍后重试 |
| `rate_limited` | 尚未到设定间隔，按 `retry_after_seconds` 等待 |
| `discarded` | 请求已取消、连接已断开或配置变更使结果失效 |

Unity 仅在空闲时按 30–900 秒间隔调度，默认 90 秒；聊天/设置面板打开、输入、拖动、模型回复、朗读、录音/转写或宠物隐藏时暂停。关闭开关立即取消客户端在途观察和待播回复；保存关闭或更换模型配置会使后端在途结果失效，客户端断开也会取消等待。

后端每次只截取一张主屏幕图片并在内存中编码，发送给用户配置的模型；不保存截图，不读写会话历史或长期记忆，不自动执行电脑命令。模型默认静默，只有值得回应时才提供短句，命令字段即使出现在模型输出中也会被丢弃。后端限制同时只有一个观察与设定的最短间隔；同一请求 ID 的已完成结果在内存中最多缓存 32 条用于去重，响应设置 `Cache-Control: no-store`。

### 会话历史

设置 `save_conversation_history: true` 后保存完整成功轮次。同一 `session_id` / `request_id` 的用户与助手消息原子保存，重复请求不会重复插入；关闭开关后不再保存新的轮次，已有会话仍可读取、继续与删除。

| 接口 | 作用 |
| --- | --- |
| `GET /api/conversations` | 返回 `conversations` 列表，含 id、title、updated_at、message_count |
| `GET /api/conversations/{session_id}` | 返回 session_id 与 messages，用于加载会话 |
| `DELETE /api/conversations/{session_id}` | 删除所选会话及其消息；不存在时返回 404 |

仅启用开关不会自动把开关启用前的客户端缓存补写为历史。新会话通过新的 `session_id` 开始；未保存的会话不会出现在列表中。文件分析接口当前不经过这套聊天入库流程。

`messages.interaction_zone` 标记自动触摸用户行。默认历史 GET 与列表 `message_count` 过滤带标记的自动用户行，保留助手回复；后端内部读取模型上下文时仍包含触摸提示。过滤以交互字段为依据，不根据“摸了…”等中文字符串判断，避免影响同文的手动输入。

### 显式长期记忆

长期记忆以 SQLite FTS5 检索中文重叠双字词和英文词，不运行嵌入模型或向量服务。`enable_long_term_memory` 默认关闭；打开后，普通聊天检索最多 5 条相关记忆，注入上下文的总量限制为 1,500 字符。记忆作为可能过时的资料使用，不作为覆盖当前用户输入的指令。

只有用户明确的“记住…”类请求才会在完整回复后自动保存，例如“记住饮食偏好：不吃香菜”；也可以手动管理。自动提取不读取模型生成的事实、不推断整段聊天中的个人画像。常见密钥/密码内容会被拒绝。条目支持名称与内容去重，同名事实可被更新。

| 接口 | 作用 |
| --- | --- |
| `GET /api/memories` | 返回 `memories`，含 id、key、content、category、source、session_id、request_id、created_at、updated_at |
| `POST /api/memories` | 手动新增，正文 `{ "key": "饮食偏好", "content": "不吃香菜", "category": "preference" }` |
| `PUT /api/memories/{memory_id}` | 用相同正文结构修改所选条目 |
| `DELETE /api/memories/{memory_id}` | 删除条目及检索索引；不存在时返回 404 |

`source` 区分 `manual` 与 `explicit_user`，后者保留关联会话/请求 ID。名称最多 100 字符、内容最多 1,000 字符、分类最多 40 字符。关闭长期记忆停止聊天中的检索与自动保存，手动 CRUD 仍可使用；关闭开关不会清空原数据。

### 文件分析

`POST /api/files/analyze`，JSON `{ "path": "D:\\Documents\\example.txt", "message": "请总结这个文件" }`，返回 `ChatResponse`。

支持绝对路径的文本、代码、JSON、CSV、PNG/JPG/WebP；最多 8 MB，文本取前 30,000 字符并明确标注截断。不会执行文件内容、访问 UNC 网络路径或修改原文件。图片要求配置视觉模型。

### 语音合成

`POST /api/tts`，JSON `{ "text": "你好" }`。可选 `voice` 覆盖当前请求的 Edge/OpenAI 音色；GPT-SoVITS 使用参考音频设置。成功响应为原始音频字节，后端根据内容返回 `audio/mpeg`（MP3）或 `audio/wav`，前端应按响应类型解码。

| 提供商 | 设置方式 | 实际请求与音频 |
| --- | --- | --- |
| Edge | `tts_provider: "edge"`，填写 `default_tts_voice` | 无需 API Key；联网生成 MP3，默认超时 40 秒 |
| OpenAI 兼容 | `tts_provider: "openai"`，填写 TTS Base URL、密钥、模型和音色 | Base URL 后追加 `/audio/speech`；发送 `model`、`input`、`voice`、`response_format: "mp3"` |
| GPT-SoVITS | `tts_provider: "gpt_sovits"`，填写参考音频路径、文本和语言 | 调用 `api_v2.py` 的 `POST /tts`，请求非流式 WAV |

OpenAI 兼容 Base URL 通常包含 `/v1`；不同服务支持的模型、音色须按服务端实际部署填写。GPT-SoVITS 的 `tts_base_url` 留空时使用 `http://127.0.0.1:9880`，也可填写完整 `/tts` 地址。参考音频路径必须由 **GPT-SoVITS 服务进程**访问，例如远程 Linux 服务的 `/srv/voices/ref.wav`；不要求它存在于桌面宠物后端机器上，也不会由本后端上传文件。

GPT-SoVITS JSON 字段为 `text`、`text_lang`、`ref_audio_path`、`prompt_text`、`prompt_lang`、`media_type: "wav"`、`streaming_mode: false`。它需要已启动且已加载所需模型的 `api_v2.py` 服务；适配器不下载或切换模型权重。参考文本应与参考音频内容一致；语言代码应符合部署版本的要求。

每次最多 3,000 字符，输出最多 10 MB。空音频、非 MP3/WAV 响应、认证失败、依赖缺失或服务不可用均会明确报错，不切换提供商伪装成功。

### 语音识别

`POST /api/asr`，`Content-Type: audio/wav`，正文直接发送 PCM WAV 字节（不是 JSON/Base64/multipart）。支持单/双声道、最多 120 秒和 8 MB，成功返回 `{ "text": "识别出的文字" }`。当前 Unity 客户端录音最长约 30 秒，识别成功后直接发送聊天。

识别使用 `asr_base_url`、`asr_api_key`、`asr_model`，Base URL 后追加 `/audio/transcriptions`。后端向提供商发送 multipart 表单，其中 `model` 为配置模型、`file` 为 `recording.wav`。接口须支持 OpenAI 兼容音频转写并返回 `text` 字段。未填写独立配置时才按前述规则沿用聊天接口；无有效识别文字和提供商错误会返回非 2xx 状态码。

“语音”页的全局按住说话由 Windows 独立客户端实现，默认关闭、默认 Ctrl+Alt+V，可设置并保存其他组合键。按住录音，松开任一按键结束并调用同一 ASR 接口，识别成功后发送聊天；配置按键时不录音，关闭该功能会取消它发起的录音。开关和组合键由客户端管理，后端没有全局键盘监听。

可选唤醒词由 Windows 客户端识别，默认关闭。唤醒后的一次录音才送入本接口；说话后的静音结束录音，长时间未说话会放弃本轮。录音、转写、模型回复和朗读期间暂停唤醒监听。后端没有持续监听麦克风的服务，实际唤醒效果仍依赖 Windows 语音语言包、麦克风权限与设备。

### 明确执行电脑操作

`POST /api/actions/execute`，JSON `{ "command": { "type": "open_app", "target": "notepad", "args": {} } }`。

- 聊天返回的 `command` 只是建议，后端不会自动执行。
- 应用与网址都服从 `enable_real_rpa`。关闭时返回 `ok: true, executed: false, dry_run: true` 及预览信息。
- `open_app` 只运行固定名单中的可执行文件，不接受 shell 脚本或额外命令行。
- `open_url` 只允许 http(s)，拒绝 `file:`、脚本协议和控制字符。
- `screen_analyze` 返回实际分析内容；`tts_speak` 返回实际生成的 `audio_base64` 与 `media_type`，常规 UI 宜直接调用 `/api/tts`。
- 结果包含 `ok`、`executed` 和可读的 `message`，失败不谎报完成。

## 角色卡与日志

角色卡把名称、人设、模型选择、动作库和语音配置放在一起，默认角色的 ID 为 `default`。当前角色与角色卡同步提交；保存或加密失败时保留更新前的状态。

| 接口 | 正文与返回 |
| --- | --- |
| `GET /api/characters` | `{ "active_character_id": "default", "cards": [...] }` |
| `GET /api/characters/{id}` | `{ "card": {...} }` |
| `POST /api/characters` | 创建角色；部分字段可省略，返回 `{ "card": {...} }` |
| `PUT /api/characters/{id}` | 部分更新角色，返回 `{ "card": {...} }`；编辑当前角色会同时更新当前配置 |
| `POST /api/characters/{id}/activate` | 返回 `{ "card": {...}, "settings": {...} }`，供前端同步模型、动作、名称和语音 |
| `POST /api/characters/{id}/skins/{skin_id}/activate` | 切换当前角色的已保存皮肤，返回 `{ "card": {...}, "settings": {...} }`；保留角色身份、语音、聊天和记忆 |
| `DELETE /api/characters/{id}` | 返回 `{ "ok": true, "id": "..." }`；默认角色和当前角色不可删除 |
| `GET /api/logs?after_id=0&limit=200&level=` | 返回 `{ "entries": [...], "last_id": 123 }` |
| `DELETE /api/logs` | 清空当前进程日志，返回 `{ "ok": true }` |

角色卡字段为 `id`、`name`、`persona`、`model_id`、`skins`、`active_skin_id`、`allow_ai_skin_switch`、`actions`，以及现有设置中的 `default_tts_voice`、全部 `tts_*`、`asr_*`、`gpt_sovits_*` 字段。LLM、保存开关和电脑操作开关是全局设置。角色 ID 创建时统一转为小写；新建时不填写 `id` 则自动生成。最多保存 100 张卡片，名称最多 64 字、人设最多 8,000 字。`model_id` 是当前皮肤使用的模型库标识，空值表示内置模型；模型资源仍由 `/api/models` 管理。

`skins` 每项为 `{ "id": "default", "name": "默认服饰", "model_id": "builtin-lita" }`，每张卡保留 1–32 套，`active_skin_id` 必须引用其中一项；旧卡缺少皮肤时从原 `model_id` 生成默认服饰。客户端先加载目标模型，成功后才调用皮肤激活接口，失败保留原外观。`allow_ai_skin_switch` 默认允许，可在角色卡关闭；模型回复可携带 `skin_id`，后端只保留当前卡已保存且仍允许自动换装的皮肤 ID。此建议不会下载模型，也不直接触发后端切换，由客户端完成加载和提交。

`actions` 使用客户端动作库结构：`{ "id": "custom_...", "label": "点头回应", "enabled": true, "builtIn": false, "speed": 1, "trigger": "nod", "steps": [{ "action": "nod", "seconds": 2 }] }`。内置动作使用已有 ID，后端强制其为内置；它们只能调整启用状态。最多 18 项内置条目和 64 项自定义动作，每个组合 1–8 步、每步 0.5–10 秒、速度 0.5–2 倍。空动作库由客户端恢复默认动作。此格式用于本项目角色卡，尚不支持外部酒馆 PNG 卡。

导入动作使用同一条目结构，另存 `motion_file`（64 位 SHA256 文件名加 `.vmd`）、`source_format: "vmd"` 和原速 `duration`，`steps` 为空。VMD 文件由客户端导入和播放，后端只管理元数据，不接收文件内容或执行动作。可导入 VMD 0002，最大 32 MiB、180 秒、播放速度 0.5–2 倍；支持兼容骨架的 FK 骨骼旋转、插值和受限中心高度变化，未执行足 IK、表情、镜头或 MMD 刚体/碰撞物理，FBX 动作文件尚未支持。依赖 IK 的舞蹈应先烘焙为 FK，不能用骨骼匹配数量推断完整舞蹈兼容性。导入条目可预览、启用、关闭、删除或绑定一个内置回应触发。

角色卡 TTS/ASR 密钥使用当前用户 Windows DPAPI 加密，不写明文，不在任何读接口回显；用 `tts_api_key_configured`、`asr_api_key_configured` 表示已配置。空密钥表示保留，`clear_tts_api_key`、`clear_asr_api_key` 显式清除。新建卡片不复制其他角色的密钥。模型切换前客户端应完成资源加载预检，成功后再激活角色卡。

聊天、文件分析、朗读请求可附 `character_id`；ASR 使用查询参数 `/api/asr?character_id=...`。与当前角色不匹配时返回 409，防止旧界面在切换后继续发起请求。`PUT /api/settings` 可带 `active_character_id` 校验正在编辑的角色，该字段不能代替激活接口。聊天、SSE 的 start/delta/final 和 ASR 返回角色 ID；前端应在切换时停止旧请求与语音，并拒绝显示旧角色结果。

每轮聊天绑定开始时的角色和接口配置，切换期间完成的回复仍归属原角色。会话与记忆按角色隔离，列表、检索和编辑接口只访问当前角色的数据。删除角色卡不会连带删除会话或记忆；需要删除时使用对应管理接口。前端切换时应清空当前聊天上下文，再载入该角色会话。

所有会话和记忆的列表、读取、新增、更新、删除接口均接受 `?character_id=当前角色ID` 并在成功响应最外层返回 `character_id`。旧角色的延迟请求在切换后抵达时返回 409；请求执行期间绑定开始时的角色数据库。前端还应核对响应角色，防止旧列表或操作提示覆盖新角色页面。

记忆读写新增 `perspective: "user" | "self"`，默认 `user` 保留原语义。用户手动选择 `self` 的条目将当前角色名称指代改写为“我”；此后改名无需修改这些记忆。用户和角色的同文记忆分别去重，避免“我喜欢红茶”混为同一条。模型提示明确区分两种视角，角色资料不会跨角色检索。旧记录不根据文字猜测视角或批量改写。

工作区记忆参考 Nekro Agent 的协作摘要与 Markdown 文档分层，提供角色摘要和按主题组织的文档，与结构化事实记忆配合使用。Unity “记忆”页提供摘要和文档编辑，只有点击对应保存按钮才生效；未保存草稿不会进入模型上下文。摘要固定为角色自身视角，文档可选用户资料或角色自身。模型没有工作区记忆写工具，不会自动改写摘要或接受文件路径。

| 工作区记忆接口 | 正文与返回 |
| --- | --- |
| `GET /api/workspace-memory/summary` | `{character_id, summary}`；未保存时正文和 `revision` 为空 |
| `PUT /api/workspace-memory/summary` | `{content, revision}`；保存空正文可清除摘要，最多 4,000 字符 |
| `GET /api/workspace-memory/documents` | `{character_id, documents}`；仅返回目录元数据，最多 64 份文档 |
| `POST /api/workspace-memory/documents` | `{title, content, perspective}`；返回 `{character_id, document}`，正文最多 12,000 字符 |
| `GET /api/workspace-memory/documents/{id}` | 返回完整 `{character_id, document}` |
| `PUT /api/workspace-memory/documents/{id}` | `{title, content, perspective, revision}`；必须携带读取到的版本 |
| `DELETE /api/workspace-memory/documents/{id}` | JSON `{revision}`；返回 `{character_id, ok, id}` |

这些接口均支持 `?character_id=当前角色ID`，旧角色请求返回 409；其他角色的文档 ID 在当前角色下不会命中。版本已变化的保存或删除返回 409，保留服务端已有更新。文件使用原子替换，路径、符号链接、目录联接、凭据内容及非手动来源被拒绝。

仅在 `enable_long_term_memory` 开启、普通聊天而非触摸事件时读取工作区资料。摘要进入上下文最多 900 字符，文档用保存时生成的有限关键词索引匹配当前话题，只读取最多两份命中文档正文，并从关键词附近提取最多约 600 字符的相关片段；摘要和文档的提示文本合计最多 2,200 字符。资料以转义 JSON 数据块标明来源和视角，不授予操作权限、不覆盖当前指令。当前阶段使用本地关键词检索，无向量服务或自动整理任务；超过索引范围的长文可按主题拆分成文档。

工作区记忆回归测试覆盖摘要与文档编辑、保存版本冲突、角色隔离和面板字段绑定。Unity 检查入口为 `PetWorkspaceMemoryChecks`，使用应用标识为 `desktop-pet-backend-qa` 的独立测试后端。

日志只保留当前进程最近 1,000 条，重启清空。每条含 `id`、UTC `timestamp`、`level`（INFO/WARNING/ERROR）、`source` 和 `message`。记录路由模板、状态、耗时和脱敏服务错误，不记录聊天、截图、文件内容、音频或查询参数；日志接口自身与健康轮询不写日志。`limit` 为 1–500；初次请求取最近记录，后续用 `after_id` 向前分页，`level` 可为空或筛选单个等级。

## 当前角色的动态动作与技能工具

`CharacterAction.ai_decide` 默认 `false`，表示该自定义组合或 VMD 是否允许模型自主选择；不能同时设置固定 `trigger`。内置动作按启用状态和已加载模型能力提供。工具 `get_available_actions()` 返回目录元数据，`play_action(action_id)` 只在本次模型请求中保存一个待播动作；最终 `ChatResponse.action_id` 随完整回复返回，客户端复核角色、启用状态、模型能力和文件后播放一次。工具本身没有即时动画、全局队列或跨角色副作用。

- `GET /api/pet-actions`：当前角色可自主调用的动作目录。
- `POST /api/pet-actions/capabilities`：客户端报告 `{character_id,model_id,actions,has_sword,model_ready}`；报告有时效，物理能力按角色和模型匹配，动作开关与 AI 选项按最新配置过滤。
- `POST /api/pet-actions/play`：提交 `{character_id,action_id}` 做同样的选择验证，返回 `ok` 与 `status: deferred_until_final_reply`。**这个独立 HTTP 接口只返回意图，不会驱动客户端动画**；动画的端到端调用应通过正常 `/api/chat` 或 `/api/chat/stream` 的模型工具回合，让 `action_id` 进入最终回复。

例如，模型读取目录后调用 `play_action`，参数为 `{"action_id":"custom_..."}`，程序回传待播结果并让模型完成回复；最终传输 `{"text":"好呀，在听。","action":"idle","action_id":"custom_..."}`，客户端才启动该自定义动作。未知、关闭、未设置 `ai_decide` 的自定义项、缺失 VMD、缺失模型或不具备太刀附件的剑动作不进入目录；强制腿部/低好感度遮挡反馈不会被工具选择替换。

同一接口还提供 `get_available_skills()` 与 `read_skill(skill_id)`，按当前角色读取启用的技能书，正文上限 12,000 字。模型连接不支持工具参数时，移除不支持的参数后使用同一连接的 JSON 后备协议：`action_id` 选择动作，`{"read_skill_id":"已有技能标识"}` 请求内部读取，再继续生成最终 JSON。目录摘要仍进入模型提示；不会静默忽略技能或改动服务商、地址、模型和密钥。最多三轮完成请求，未知工具、重复调用、无效参数与关闭/旧角色技能会被拒绝。

## WebSocket

`ws://127.0.0.1:18080/ws` 支持：`ping`、`chat`、`execute`、`get_settings`、`update_settings`。

```json
{"type":"chat","message":"你好","include_screen_context":false}
```

回应 `{ "type": "chat_response", "payload": { ...ChatResponse } }`。设置同样不回显密钥。单条消息错误会返回 `error` 事件，连接仍能继续处理下一条消息。来自外部网页的 Origin 和非本地主机请求被拒绝。

## 验证范围

回归测试覆盖离线聊天、SSE 与中止、低好感度触摸边界、SQLite 会话、显式记忆与检索、DPAPI 凭据保护、RPA、Origin、WebSocket、文件、语音协议，以及主动看屏的默认关闭、频率/并发限制、取消、静默/短句与数据隔离。模型库检查包括包复制、无效包/清单、路径限制和运行时模型数据解析；角色卡另有皮肤与导入动作元数据校验。语音与 AI 模型协议测试使用 OpenAI SDK 配合 `httpx.MockTransport` 等替身检查请求地址、JSON/multipart、密钥隔离、脱敏和错误处理，不需要连接外部模型服务。

LLM、视觉、ASR、TTS 和 GPT-SoVITS 的集成测试需要对应服务环境。麦克风、唤醒词、长时间运行及多屏/DPI 行为需要在 Windows 客户端中验证。


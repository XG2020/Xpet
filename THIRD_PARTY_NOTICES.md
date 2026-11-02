# 第三方声明

Xpet 的原创代码与文档采用根目录 [MIT License](LICENSE)。第三方软件和素材遵循各自许可证，项目的 MIT 许可证不授予第三方内容的版权或商标权。

下文列出的开发文件路径均相对于源码仓库。本声明同时适用于项目提供的便携程序。

## Unity 与字体

- Unity Editor、URP、Input System、uGUI / TextMesh Pro 等组件适用各自随包提供的许可证及 Unity 条款。依赖版本由 `pet-frontend/pet-frontend/Packages/manifest.json` 声明。
- `Assets/TextMesh Pro/` 中的着色器和资源保留其上游版权。
- Liberation Sans 字体及其 SDF 资源采用 **SIL Open Font License 1.1**。完整许可文件为 `pet-frontend/pet-frontend/Assets/TextMesh Pro/Fonts/LiberationSans - OFL.txt`，版权声明包括 Google Corporation 与 Red Hat, Inc.。分发包含该字体的程序时应附带此许可。
- Microsoft YaHei、Microsoft JhengHei 等系统字体适用各自字体许可证。字体生成与嵌入不会改变其授权条件。

## Python 与构建依赖

运行依赖由 `pet-backend/requirements.txt` 声明，构建依赖由 `pet-backend/requirements-build.txt` 声明。

FastAPI、Pydantic Settings、OpenAI Python SDK、Uvicorn、HTTPX、python-dotenv、edge-tts、MSS、assimp_py、pywin32、PyInstaller 及其传递依赖保留各自版权。具体许可证以安装版本附带的 LICENSE / NOTICE 为准。

分发二进制程序时，应遵守 PyInstaller 的分发例外，并附带 Assimp 原生库、Python 运行时及其他依赖要求的声明。

项目兼容的 AI 与语音服务由各自提供方运营，服务使用遵循相应服务条款。

## 模型、动作与其他素材

源码不附带 3D 模型与角色素材。使用和分发模型、贴图、动作及其他素材时，需取得对应授权并保留要求的署名、许可和修改记录。转换格式或适配骨骼不会改变原素材许可。

文档中的角色或产品名称用于说明兼容功能，不表示相应权利人授权或背书。角色接入要求见源码中的 `docs/ASSETS.md`。

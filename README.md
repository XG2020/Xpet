# Xpet · 3D 桌面宠物

使用 **Unity 6.5 + FastAPI** 构建的 Windows 3D 桌面宠物，当前版本 **0.6.0**。支持角色互动、对话、语音、角色卡、模型导入与记忆。

本仓库提供客户端、后端和构建工具源码。**源码不附带 3D 模型与角色素材**，运行完整客户端前需要准备获授权的角色资源，详见[素材说明](docs/ASSETS.md)。

## 功能

- 透明桌面窗口、鼠标穿透、拖拽、系统托盘与多显示器支持。
- 角色卡和多套外观，PMX / FBX / GLB / `.petmodel` 导入，兼容 VMD 的 FK 动作播放。
- 流式对话、离线体验、角色动作与表情、可编辑技能书。
- 语音输入与朗读、Windows SAPI 识别、按住说话与可选唤醒词。
- 按角色管理聊天与记忆；可选截图分析与电脑操作建议。

完整操作与功能边界见[使用指南](docs/USER_GUIDE.md)。任意骨架、完整 MMD 物理和 VMD IK / 镜头轨道目前不在支持范围内。

## 从源码开始

环境：**Windows x64、Python 3.11+、Unity 6000.5.10f1**。Unity 版本记录在 [ProjectVersion.txt](pet-frontend/pet-frontend/ProjectSettings/ProjectVersion.txt)。

在项目根目录打开 PowerShell，安装并启动后端：

```powershell
powershell -ExecutionPolicy Bypass -File pet-backend/setup.ps1
powershell -ExecutionPolicy Bypass -File pet-backend/start.ps1
```

后端地址为 `http://127.0.0.1:18080`，健康检查为 `http://127.0.0.1:18080/health`。未配置聊天接口时可使用离线体验；使用外部服务时，在客户端设置中填写接口地址、模型名称和密钥。

通过 Unity Hub 打开 **`pet-frontend/pet-frontend`**，等待包还原与脚本编译。准备角色资源并生成场景后，即可开发和构建客户端。具体步骤见[开发指南](docs/DEVELOPMENT.md)。

## 项目结构

```text
pet-backend/                 FastAPI 服务、依赖与后端测试
pet-frontend/pet-frontend/   Unity 工程（Assets / Packages / ProjectSettings）
tools/                      模型转换、构建、打包与验证工具
branding/                   项目图标
pet-models/                 模型库说明
pet-actions/                动作库说明
pet-skills/                 技能库说明
docs/                       使用、开发、素材与发布指南
```

## 文档与贡献

- [使用指南](docs/USER_GUIDE.md)：客户端操作、接口设置和功能限制。
- [开发指南](docs/DEVELOPMENT.md)：环境准备、测试、Unity 工程与打包。
- [后端说明](pet-backend/README.md) / [客户端说明](pet-frontend/README.md)：模块与接口。
- [发布指南](docs/OPEN_SOURCE.md)：源码发布步骤。
- [贡献指南](CONTRIBUTING.md)：问题反馈与代码贡献。

## 许可证

本项目原创代码采用 [MIT License](LICENSE)。第三方依赖、模型、贴图、动作与字体遵循各自许可证。详见[第三方声明](THIRD_PARTY_NOTICES.md)和[素材说明](docs/ASSETS.md)。

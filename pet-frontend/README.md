# Unity 客户端

通过 Unity Hub 打开本目录下的 **`pet-frontend/`**，即仓库根目录的 `pet-frontend/pet-frontend`。该工程包含 `Assets`、`Packages` 和 `ProjectSettings`，Unity 版本为 **6000.5.10f1**。

源码不附带 3D 模型。先准备获授权模型及对应 prefab，再使用 `Desktop Pet / Build Desktop Pet Scene` 创建场景。模型库初始化和运行时模型加载依赖默认角色的组件、材质与动画模板；`Build Lita Assets` 使用特定丽塔 FBX 与材质表，不是通用模型导入入口。完整步骤见[开发指南](../docs/DEVELOPMENT.md)。

## 主要模块

| 模块 | 用途 |
| --- | --- |
| `PetBackendHttpClient` | 聊天、流式回复、屏幕分享、语音与文件分析 |
| `BackendSettingsApi` / `BackendSettingsPanel` | 后端配置与设置界面 |
| `PetAnimatorBridge` / `PetCharacterAnimation` | 动作、骨骼动画与表情 |
| `TransparentWindowController` | 透明置顶、鼠标穿透与窗口拖动 |
| `DesktopPetUIActions` / `DesktopPetInteraction` | 菜单、交互、录音与文件拖入 |

`Assets/Editor` 中的 `Desktop Pet` 菜单提供场景创建、资源生成和检查工具。现有场景的扩展菜单通常要求先打开 `Assets/Scenes/DesktopPet.unity`，具体前置条件见对应脚本。

Windows 客户端使用 **StandaloneWindows64、Mono、Direct3D 11**。输出目录为工程内 `Builds/Windows`，可执行文件名为 `Xpet.Client.exe`；之后由根目录 `tools/package_desktop_pet.ps1` 组合便携发行包。

透明合成、鼠标穿透、托盘、语音设备和多显示器功能需要在 Windows 独立客户端中验证。完整功能与兼容限制见[使用指南](../docs/USER_GUIDE.md)。

# 开发指南

下列 PowerShell 命令均从仓库根目录运行，另有说明的除外。Unity 内的 `Assets/...` 路径相对于 `pet-frontend/pet-frontend`。

## 环境

| 工具 | 要求 |
| --- | --- |
| 系统 | Windows x64 |
| Python | 3.11 或更新版本 |
| Unity | 6000.5.10f1，安装 Windows Build Support（Mono） |
| PowerShell | Windows PowerShell 5.1 或兼容版本 |

运行后端不需要 Unity。便携程序不需要安装 Python 或 Unity；运行时模型导入不调用 Blender。

离线转换工具 `tools/convert_pet_pmx.py` 需要通过 `--mmd-tools-zip` 指定 mmd_tools 插件 ZIP。完整参数见脚本的 `--help`。

## 后端

```powershell
powershell -ExecutionPolicy Bypass -File pet-backend/setup.ps1
powershell -ExecutionPolicy Bypass -File pet-backend/start.ps1
```

安装脚本在 `pet-backend/.venv` 创建虚拟环境，并安装 [requirements.txt](../pet-backend/requirements.txt) 中的依赖。启动脚本在前台运行服务；按 `Ctrl+C` 停止。另一个终端可检查：

```powershell
Invoke-RestMethod http://127.0.0.1:18080/health
```

无需填写密钥即可开发离线对话。业务配置在客户端设置中管理；环境变量示例见 [.env.example](../pet-backend/.env.example)，API 说明见[后端文档](../pet-backend/README.md)。

后端回归测试从 `pet-backend` 目录运行：

```powershell
Set-Location pet-backend
.\.venv\Scripts\python.exe -m unittest discover -s tests -v
```

测试覆盖离线逻辑与模拟服务协议。外部 AI、语音服务和真实设备需分别联调。

## Unity 工程与角色

源码不附带 3D 模型。完整客户端需要先准备获授权的角色资源，素材要求见[素材说明](ASSETS.md)。

1. 在 Unity Hub 中打开 `pet-frontend/pet-frontend`。首次打开时等待包还原、生成 `Packages/packages-lock.json` 并完成脚本编译。
2. 准备角色 prefab，配置 Animator、`PetCharacterAnimation`、材质与碰撞体。
3. 使用 `Desktop Pet / Build Desktop Pet Scene` 生成 `Assets/Scenes/DesktopPet.unity`。
4. 打开该场景，启动后端，进入 Play 模式验证功能。
5. 在 Unity Build Profiles 的场景列表中添加生成的场景。

场景生成器默认查找 `Assets/Models/Lita/DesktopPetLita.prefab`。`PetModelLibrary` 初始化需要带有 `PetCharacterAnimation` 的内置角色，运行时模型加载也使用其材质与动画模板。缺少角色 prefab 时，生成器只建立 UI、相机和服务，不能运行完整桌宠。已有同名场景时会保留现有场景。

`Desktop Pet / Build Lita Assets` 是丽塔素材的专用生成器，要求 `Assets/Models/Lita/lita.fbx`、`PetMaterialMap.json` 及对应贴图，并依赖特定网格与骨骼布局。使用其他角色时，应检查和调整 `PetModelSetup.cs` 与 `DesktopPetSceneBuilder.cs` 中的模型假设。

场景生成器使用可用的中文字体建立 UI 字体资源。`Desktop Pet / Validate Desktop Pet Scene` 检查场景引用、字体、界面和动画。其他 `Run ... Checks` 菜单的前置条件见对应脚本，部分要求 Play 模式或已完成模型配置。

## Windows 构建与打包

先完成角色准备和场景检查，再进行以下步骤：

1. 执行 `Desktop Pet / Prepare Windows Build`，设置 Windows x64、Mono、Direct3D 11 与透明窗口选项。
2. 在 Unity Build Profiles 中构建，将客户端保存为工程内 `Builds/Windows/Xpet.Client.exe`。
3. 回到仓库根目录，编译启动器并打包后端：

```powershell
powershell -ExecutionPolicy Bypass -File tools/build_xpet_launcher.ps1
pet-backend/.venv/Scripts/python.exe -m pip install -r pet-backend/requirements-build.txt
pet-backend/.venv/Scripts/python.exe -m PyInstaller --noconfirm --distpath .artifacts/frozen-backend --workpath .artifacts/pyinstaller-build tools/XpetBackend.spec
```

4. 组合并检查便携发行目录：

```powershell
powershell -ExecutionPolicy Bypass -File tools/package_desktop_pet.ps1 -Version 0.6.0
powershell -ExecutionPolicy Bypass -File tools/verify_xpet_package.ps1 -ExpectedModelCount 1
```

默认输出为 `dist/Xpet-0.6.0`，运行其中的 `Xpet.exe`。示例假设包中包含 **1 个额外模型**，请将 `-ExpectedModelCount` 改为实际打包的模型数量；参数范围为 1–100，零模型包不适用此检查。

启动器构建需要 Windows .NET Framework C# 编译器。后端 PyInstaller 配置会收集模型解析库、证书与语音依赖，应使用仓库提供的 spec 文件。

对外发行时使用允许再分发的模型、动作、字体和依赖，并保留所需许可，详见[第三方声明](../THIRD_PARTY_NOTICES.md)。源码发布步骤见[发布指南](OPEN_SOURCE.md)。

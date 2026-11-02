# 模型与素材

源码不附带 3D 模型与角色素材。运行完整客户端前，需要准备获授权的模型、贴图及对应动画资源。

## 角色接入

Unity 工程位于 `pet-frontend/pet-frontend`，以下 `Assets/...` 路径均相对于该工程。

场景生成器默认查找 `Assets/Models/Lita/DesktopPetLita.prefab`。角色 prefab 需要配置可见模型、Animator、`PetCharacterAnimation`、材质、次级运动和交互碰撞体。`PetModelLibrary` 依赖默认角色的组件，运行时模型加载也使用其材质与动画模板。

配置角色后，执行 `Desktop Pet / Build Desktop Pet Scene` 生成场景。缺少角色 prefab 时，生成器只创建 UI、相机和服务，不能运行完整桌宠。场景与构建步骤见[开发指南](DEVELOPMENT.md)。

## 专用生成器

`Desktop Pet / Build Lita Assets` 使用特定丽塔网格与骨骼布局，要求在 `Assets/Models/Lita` 提供 `lita.fbx`、`PetMaterialMap.json` 和对应贴图。

使用其他角色时，应检查并调整 `PetModelSetup.cs` 与 `DesktopPetSceneBuilder.cs` 的模型假设。该菜单并非任意模型的通用生成器。运行时 PMX / FBX / GLB 导入及 VMD 动作兼容范围见[使用指南](USER_GUIDE.md#角色模型库)。

## 字体

UI 生成器依次尝试 Microsoft YaHei、Microsoft JhengHei 和 Noto Sans CJK SC，开发系统需要具备至少一种可用的中文字体。使用和分发字体需遵守其许可。

项目包含的 Liberation Sans 字体及其 SDF 资源采用 SIL Open Font License 1.1，完整许可随字体提供。

## 素材授权

模型、贴图、动作和字体遵循各自许可证。对外发布时应使用允许再分发的素材，并保留要求的署名、许可和修改记录。格式转换不会改变原素材的授权条件；项目代码的 MIT 许可证不授予第三方素材版权。详见[第三方声明](../THIRD_PARTY_NOTICES.md)。

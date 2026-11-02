# 贡献指南

欢迎提交问题反馈和 Pull Request。请先阅读 [README](README.md)、[开发指南](docs/DEVELOPMENT.md)及相关模块说明。

## 问题反馈

请提供预期行为、实际行为、复现步骤，以及 Windows、Unity 和 Xpet 版本。模型问题需说明文件格式和骨骼特点；附件应具备相应分享许可。

## 提交修改

- 每个 Pull Request 聚焦一项修改，并说明解决的问题、行为变化和验证方式。
- 修改后端逻辑时，使用项目虚拟环境，在 `pet-backend` 目录运行 `python -m unittest discover -s tests -v`。
- 修改 Unity 资源时保留对应 `.meta` 文件。
- 窗口交互、语音和模型表现的变化需要在对应平台验证，并说明测试覆盖范围。
- 同步更新相关文档，确保示例与实际接口一致。

项目原创代码采用 [MIT](LICENSE)。新增第三方依赖应记录来源和许可证，角色、贴图、动作及字体的授权独立处理，参见[第三方声明](THIRD_PARTY_NOTICES.md)。

# 构建与发布

环境安装、角色准备和 Windows 客户端构建见[开发指南](DEVELOPMENT.md)。源码不附带 3D 模型，完整客户端所需素材见[素材说明](ASSETS.md)。

## 生成源码包

需要 Python 3.11+ 和 Git。在项目根目录运行：

```powershell
python tools/export_source.py
```

也可使用项目虚拟环境：

```powershell
pet-backend/.venv/Scripts/python.exe tools/export_source.py
```

默认输出：

```text
dist/Xpet-source-0.6.0/               源码目录
dist/Xpet-source-0.6.0.zip            源码压缩包
dist/Xpet-source-0.6.0.manifest.json  文件大小与 SHA256 清单
```

需要指定输出目录时，可使用：

```powershell
python tools/export_source.py --destination dist/Xpet-source-0.6.0-r2
```

## 发布到 GitHub

在 GitHub 创建空仓库，例如 `Xpet`，使用项目已有的 README、许可证和 `.gitignore`。从项目根目录进入源码包目录，初始化并提交：

```powershell
Set-Location dist/Xpet-source-0.6.0
git init -b main
git add .
git diff --cached --stat
git status --short
git commit -m "Initial open source release"
```

将 `YOUR_ACCOUNT` 和仓库名替换为实际值，再连接远程仓库并推送：

```powershell
git remote add origin https://github.com/YOUR_ACCOUNT/Xpet.git
git push -u origin main
```

Push 和 Pull Request 会触发 `.github/workflows/backend-tests.yml`，在 Windows + Python 3.12 上运行后端测试。Unity 客户端按[开发指南](DEVELOPMENT.md)构建和验证。

## 发布便携程序

完成客户端与后端构建后，使用 `tools/package_desktop_pet.ps1` 组合便携程序，默认输出为 `dist/Xpet-0.6.0`。检查通过后压缩完整发行目录，作为 GitHub Releases 附件提供，并填写版本说明、主要变化与运行要求。

发行包中的角色、模型、动作、字体和依赖须具备相应再分发许可；要求附带的许可证与署名应随包提供，详见[第三方声明](../THIRD_PARTY_NOTICES.md)。

# -*- mode: python ; coding: utf-8 -*-
"""Portable backend build, including the bundled Python TLS runtime data."""
from pathlib import Path
import sysconfig
from PyInstaller.utils.hooks import collect_all, collect_data_files

workspace = Path(SPECPATH).resolve().parent
datas = collect_data_files("certifi") + collect_data_files("edge_tts")
binaries = []
hiddenimports = ["uvicorn.logging", "uvicorn.loops.asyncio", "uvicorn.protocols.http.h11_impl",
                 "uvicorn.protocols.websockets.websockets_impl", "mss.windows",
                 "pythoncom", "pywintypes", "win32com.client.dynamic", "win32com.client.connect",
                 "win32com.server.policy", "win32com.server.util"]
assimp_data, assimp_binaries, assimp_imports = collect_all("assimp_py")
datas += assimp_data
binaries += assimp_binaries
hiddenimports += assimp_imports

# The bundled Codex Python ssl.SSLContext reads this beside ssl.py, before
# loading the normal CA bundle. Preserve its certificate policy in frozen apps.
# Standard Python distributions do not have or need this additional resource.
tls_runtime_data = Path(sysconfig.get_path("stdlib")) / "codex-rejected-cas.pem"
if tls_runtime_data.is_file():
    datas.append((str(tls_runtime_data), "."))

a = Analysis([str(workspace / "pet-backend" / "desktop_pet_server.py")],
             pathex=[str(workspace / "pet-backend")], binaries=binaries, datas=datas,
             hiddenimports=hiddenimports, hookspath=[], hooksconfig={}, runtime_hooks=[],
             excludes=[], noarchive=False, optimize=0)
pyz = PYZ(a.pure)
exe = EXE(pyz, a.scripts, [], exclude_binaries=True, name="XpetBackend", debug=False,
          bootloader_ignore_signals=False, strip=False, upx=True, console=False,
          disable_windowed_traceback=False, argv_emulation=False, target_arch=None,
          codesign_identity=None, entitlements_file=None, icon=str(workspace / "branding" / "Xpet.ico"))
coll = COLLECT(exe, a.binaries, a.datas, strip=False, upx=True, upx_exclude=[], name="XpetBackend")

"""Current-user Windows DPAPI storage for local provider credentials."""
from __future__ import annotations

import base64
import binascii
import ctypes
from ctypes import wintypes
import os
from collections.abc import Mapping


CREDENTIAL_FIELDS = ("openai_api_key", "tts_api_key", "asr_api_key")
STORAGE_VERSION = 1
STORAGE_PROVIDER = "windows-dpapi-current-user"
_CRYPTPROTECT_UI_FORBIDDEN = 0x01


class CredentialProtectionError(RuntimeError):
    """A safe error that never includes a credential or native API payload."""


class UnsupportedCredentialPlatform(CredentialProtectionError):
    pass


class _DataBlob(ctypes.Structure):
    _fields_ = [("cbData", wintypes.DWORD), ("pbData", ctypes.POINTER(ctypes.c_ubyte))]


def _blob(value: bytes) -> tuple[_DataBlob, ctypes.Array]:
    buffer = ctypes.create_string_buffer(value)
    return _DataBlob(len(value), ctypes.cast(buffer, ctypes.POINTER(ctypes.c_ubyte))), buffer


def _crypt(value: bytes, field: str, *, decrypt: bool = False) -> bytes:
    if os.name != "nt":
        raise UnsupportedCredentialPlatform("密钥安全存储仅支持 Windows DPAPI，未保存明文密钥。")
    source, source_buffer = _blob(value)
    entropy, entropy_buffer = _blob(("desktop-pet/settings/v1/" + field).encode("utf-8"))
    output = _DataBlob()
    local_free = None
    try:
        crypt32 = ctypes.WinDLL("crypt32", use_last_error=True)
        kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
        local_free = kernel32.LocalFree
        local_free.argtypes = [ctypes.c_void_p]
        local_free.restype = ctypes.c_void_p
        operation = crypt32.CryptUnprotectData if decrypt else crypt32.CryptProtectData
        operation.argtypes = [
            ctypes.POINTER(_DataBlob),
            ctypes.POINTER(wintypes.LPWSTR) if decrypt else wintypes.LPCWSTR,
            ctypes.POINTER(_DataBlob), ctypes.c_void_p, ctypes.c_void_p,
            wintypes.DWORD, ctypes.POINTER(_DataBlob),
        ]
        operation.restype = wintypes.BOOL
        # No machine-wide flag: only the current Windows user's DPAPI can decrypt.
        if not operation(ctypes.byref(source), None, ctypes.byref(entropy), None, None,
                         _CRYPTPROTECT_UI_FORBIDDEN, ctypes.byref(output)):
            raise CredentialProtectionError("Windows 无法处理受保护的密钥，请使用保存它的 Windows 账户。")
        if not output.pbData or not output.cbData:
            raise CredentialProtectionError("Windows 返回了无效的受保护密钥数据。")
        return ctypes.string_at(output.pbData, output.cbData)
    except CredentialProtectionError:
        raise
    except Exception:
        raise CredentialProtectionError("Windows 密钥保护失败，未保存明文密钥。") from None
    finally:
        ctypes.memset(source_buffer, 0, ctypes.sizeof(source_buffer))
        ctypes.memset(entropy_buffer, 0, ctypes.sizeof(entropy_buffer))
        if output.pbData and local_free is not None:
            ctypes.memset(output.pbData, 0, output.cbData)
            local_free(ctypes.cast(output.pbData, ctypes.c_void_p))


def protect_credentials(credentials: Mapping[str, str]) -> dict:
    """Return a versioned ciphertext map; fail instead of falling back to plaintext."""
    encrypted = {}
    for field in CREDENTIAL_FIELDS:
        value = credentials.get(field, "")
        if value:
            encrypted[field] = base64.b64encode(_crypt(value.encode("utf-8"), field)).decode("ascii")
    return {"version": STORAGE_VERSION, "provider": STORAGE_PROVIDER, "values": encrypted}


def unprotect_credentials(record: object) -> dict[str, str]:
    """Read only the known version and format; malformed data must never be ignored."""
    error = "受保护的密钥格式无效或无法解密，原设置文件未改动。"
    if not isinstance(record, dict) or set(record) != {"version", "provider", "values"}:
        raise CredentialProtectionError(error)
    if type(record["version"]) is not int or record["version"] != STORAGE_VERSION:
        raise CredentialProtectionError(error)
    if record["provider"] != STORAGE_PROVIDER or not isinstance(record["values"], dict):
        raise CredentialProtectionError(error)
    values = record["values"]
    if set(values) - set(CREDENTIAL_FIELDS):
        raise CredentialProtectionError(error)
    result = {field: "" for field in CREDENTIAL_FIELDS}
    for field, encoded in values.items():
        if not isinstance(encoded, str) or not encoded:
            raise CredentialProtectionError(error)
        try:
            encrypted = base64.b64decode(encoded, validate=True)
            if not encrypted:
                raise ValueError
            result[field] = _crypt(encrypted, field, decrypt=True).decode("utf-8")
        except (ValueError, UnicodeError, binascii.Error):
            raise CredentialProtectionError(error) from None
    return result

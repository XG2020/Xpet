using System;
using UnityEngine;

public static class DesktopPetPrefs
{
    public const string BackendBaseUrlPrefsKey = "desktop_pet.backend_base_url";
    public const string DefaultBackendBaseUrl = "http://127.0.0.1:18080";

    public static string GetBackendBaseUrl()
    {
        string[] arguments = Environment.GetCommandLineArgs();
        for (int index = 1; index + 1 < arguments.Length; index++)
            if (arguments[index] == "--backend-base-url" && IsValidBackendUrl(arguments[index + 1]))
                return arguments[index + 1].Trim().TrimEnd('/');
        string value = PlayerPrefs.GetString(BackendBaseUrlPrefsKey, DefaultBackendBaseUrl);
        return IsValidBackendUrl(value) ? value.Trim().TrimEnd('/') : DefaultBackendBaseUrl;
    }

    public static void SetBackendBaseUrl(string value)
    {
        if (!IsValidBackendUrl(value)) throw new ArgumentException("服务地址必须是有效的 http:// 或 https:// 地址。");

        PlayerPrefs.SetString(BackendBaseUrlPrefsKey, value.Trim().TrimEnd('/'));
        PlayerPrefs.Save();
    }

    public static bool IsValidBackendUrl(string value)
    {
        return Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            && string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Query)
            && string.IsNullOrEmpty(uri.Fragment);
    }
}

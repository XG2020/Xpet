using System;
using System.Text.RegularExpressions;
using UnityEngine;

/// <summary>Color separation for the supplied Evanescia/昔涟 albedo. Other characters keep
/// identity values; no source pixels, opacity, render queues, or scene lights are changed.</summary>
public static class PetImportedMaterialAppearance
{
    public static bool IsEvanescia(string modelSource)
    {
        string name = modelSource ?? "";
        return name.IndexOf("Evanescia", StringComparison.OrdinalIgnoreCase) >= 0 || name.Contains("昔涟");
    }

    public static string Configure(Material material, string modelSource)
    {
        if (material == null || material.shader == null || material.shader.name != "Desktop Pet/Soft Anime") return "unsupported";
        if (!IsEvanescia(modelSource)) return "unchanged";
        Texture texture = material.GetTexture("_BaseMap");
        string textureName = texture != null ? texture.name : "";
        string name = material.name;
        bool hair = Regex.IsMatch(textureName, "hair|髪|髮", RegexOptions.IgnoreCase);
        bool face = Regex.IsMatch(name, "^(颜|顏|顔)|_Mat_Face$", RegexOptions.IgnoreCase);
        bool skin = Regex.IsMatch(name, "肌|skin", RegexOptions.IgnoreCase);
        bool eye = !face && (Regex.IsMatch(name, "目|眉|睫|eye", RegexOptions.IgnoreCase) || name == "齿");
        bool silverDetail = Regex.IsMatch(name, "纹饰|裙链|发饰白|耳环星环|戒指|指甲", RegexOptions.IgnoreCase);
        string category;
        if (face) {
            category = "warm face";
            Set(material, new Color(0.92f, 0.77f, 0.80f), 0.38f, 0.26f, 0.95f, 1.02f, 1.035f);
        } else if (skin) {
            category = "warm skin";
            Set(material, new Color(0.90f, 0.73f, 0.76f), 0.44f, 0.21f, 0.94f, 1.03f, 1.04f);
        } else if (hair) {
            category = "pink hair";
            Set(material, new Color(0.74f, 0.63f, 0.80f), 0.48f, 0.16f, 0.94f, 1.12f, 1.08f);
        } else if (eye) {
            category = "eye detail";
            Set(material, new Color(0.88f, 0.83f, 0.92f), 0.42f, 0.18f, 0.97f, 1.08f, 1.025f);
        } else if (silverDetail) {
            category = "silver and lace detail";
            Set(material, new Color(0.89f, 0.86f, 0.93f), 0.44f, 0.18f, 0.975f, 1.025f, 1.025f);
        } else {
            category = "white and violet cloth";
            Set(material, new Color(0.79f, 0.72f, 0.86f), 0.46f, 0.16f, 0.96f, 1.075f, 1.06f);
        }
        material.SetFloat("_LightInfluence", 0.12f);
        material.SetFloat("_LightColorInfluence", 0.015f);
        material.SetFloat("_SpecularStrength", 0.008f);
        return category;
    }

    private static void Set(Material material, Color shade, float threshold, float softness, float brightness, float saturation, float contrast)
    {
        material.SetColor("_ShadeColor", shade);
        material.SetFloat("_ShadeThreshold", threshold); material.SetFloat("_ShadeSoftness", softness);
        material.SetFloat("_Brightness", brightness); material.SetFloat("_Saturation", saturation);
        material.SetFloat("_MidtoneContrast", contrast);
    }
}

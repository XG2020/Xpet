using UnityEngine;

/// <summary>All desktop values are physical pixels; Unity UI values are render pixels.</summary>
public static class DesktopPetWindowGeometry
{
    public static Vector2 NormalizeOrigin(RectInt range, Vector2Int origin)
    {
        int left = Mathf.Clamp(origin.x, range.xMin, range.xMax);
        int top = Mathf.Clamp(origin.y, range.yMin, range.yMax);
        return new Vector2(range.width > 0 ? (left - range.xMin) / (float)range.width : 1f,
            range.height > 0 ? (top - range.yMin) / (float)range.height : 1f);
    }

    public static Vector2Int RestoreOrigin(RectInt range, Vector2 normalized)
    {
        float x = float.IsNaN(normalized.x) || float.IsInfinity(normalized.x) ? 1f : Mathf.Clamp01(normalized.x);
        float y = float.IsNaN(normalized.y) || float.IsInfinity(normalized.y) ? 1f : Mathf.Clamp01(normalized.y);
        return new Vector2Int(range.xMin + Mathf.RoundToInt(x * range.width),
            range.yMin + Mathf.RoundToInt(y * range.height));
    }

    public static RectInt AllowedOrigins(RectInt workArea, Rect visible, Vector2Int clientSize, Vector2Int renderSize, int margin = 2)
    {
        float sx = clientSize.x / (float)Mathf.Max(1, renderSize.x);
        float sy = clientSize.y / (float)Mathf.Max(1, renderSize.y);
        int left = Mathf.CeilToInt(workArea.xMin + margin - visible.xMin * sx);
        int right = Mathf.FloorToInt(workArea.xMax - margin - visible.xMax * sx);
        int top = Mathf.CeilToInt(workArea.yMin + margin - (renderSize.y - visible.yMax) * sy);
        int bottom = Mathf.FloorToInt(workArea.yMax - margin - (renderSize.y - visible.yMin) * sy);
        // An oversized visible group cannot fit both edges. Keep its lower/right edge
        // recoverable instead of pinning the top and pushing feet under the taskbar.
        if (bottom < top) top = bottom;
        if (right < left) left = right;
        return new RectInt(left, top, Mathf.Max(0, right - left), Mathf.Max(0, bottom - top));
    }
}

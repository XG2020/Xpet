using System;
using System.Collections;
using UnityEngine;

/// <summary>Explicit QA only; synthetic work areas never alter the Windows display layout.</summary>
public static class DesktopPetPlacementChecks
{
    public static IEnumerator Check(Action<string> fail)
    {
        yield return Check(UnityEngine.Object.FindFirstObjectByType<TransparentWindowController>(), fail);
    }

    public static IEnumerator Check(TransparentWindowController window, Action<string> fail)
    {
        CheckGeometry(fail);
        if (window == null) fail?.Invoke("Placement: missing TransparentWindowController.");
        else window.CheckNativePlacement(fail);
        yield break;
    }

    public static void CheckGeometry(Action<string> fail)
    {
        try
        {
            var originalRange = new RectInt(-1000, -200, 800, 400);
            var originalPosition = new Vector2Int(-800, 100);
            Vector2 relative = DesktopPetWindowGeometry.NormalizeOrigin(originalRange, originalPosition);
            Require(Mathf.Abs(relative.x - .25f) < .00001f && Mathf.Abs(relative.y - .75f) < .00001f,
                "Negative desktop coordinates have incorrect normalized positions.");
            Require(DesktopPetWindowGeometry.RestoreOrigin(originalRange, relative) == originalPosition,
                "Normalized position does not round trip to the same physical pixel.");
            Require(DesktopPetWindowGeometry.RestoreOrigin(new RectInt(400, -600, 1600, 800), relative) == new Vector2Int(800, 0),
                "Changing work area origin and dimensions loses the relative position.");
            Require(DesktopPetWindowGeometry.RestoreOrigin(originalRange, new Vector2(-1, 2))
                == new Vector2Int(originalRange.xMin, originalRange.yMax), "Restored positions can escape the work area.");
            var collapsed = new RectInt(-400, 77, 0, 0);
            Vector2 collapsedRelative = DesktopPetWindowGeometry.NormalizeOrigin(collapsed, new Vector2Int(900, -200));
            Require(!float.IsNaN(collapsedRelative.x) && !float.IsNaN(collapsedRelative.y)
                && DesktopPetWindowGeometry.RestoreOrigin(collapsed, collapsedRelative) == new Vector2Int(-400, 77),
                "A zero-sized origin range cannot be recovered.");

            var visible = new Rect(430, 80, 100, 500);
            RectInt physicalRange = DesktopPetWindowGeometry.AllowedOrigins(new RectInt(-1920, 0, 1920, 1040), visible,
                new Vector2Int(960, 720), new Vector2Int(960, 720));
            Require(physicalRange == new RectInt(-2348, -138, 1816, 536),
                "Visible pet bounds do not respect a negative monitor origin and taskbar area.");
            RectInt scaledRange = DesktopPetWindowGeometry.AllowedOrigins(new RectInt(0, 0, 3840, 2080), visible,
                new Vector2Int(1920, 1440), new Vector2Int(960, 720));
            Require(scaledRange == new RectInt(-858, -278, 3636, 1076),
                "DPI scaling mixes render pixels with physical desktop pixels.");
            RectInt oversized = DesktopPetWindowGeometry.AllowedOrigins(new RectInt(0, 0, 800, 600),
                new Rect(0, 0, 1600, 1200), new Vector2Int(960, 720), new Vector2Int(960, 720));
            Require(oversized.width == 0 && oversized.height == 0 && oversized.xMax + 1600 == 798
                && oversized.yMax + 720 == 598, "An oversized pet loses its lower and right recovery edges.");
        }
        catch (Exception exception) { fail?.Invoke("Placement geometry: " + exception.Message); }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

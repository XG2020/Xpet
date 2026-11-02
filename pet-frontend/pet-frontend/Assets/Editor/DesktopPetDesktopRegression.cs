using System;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

public static class DesktopPetDesktopRegression
{
    [MenuItem("Desktop Pet/Run Desktop Boundary Regression")]
    public static void Run()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("Exit Play Mode first.");
        var report = new StringBuilder();
        try {
            var visible = new Rect(710, 55, 245, 425);
            var render = new Vector2Int(1200, 800);
            int count = 0;
            foreach (var work in new[] { new RectInt(0,0,1920,1040), new RectInt(-2560,-100,2560,1400), new RectInt(1920,0,2560,1400) })
                foreach (float scale in new[] { 1f, 1.25f, 1.5f, 2f }) {
                    var client = new Vector2Int(Mathf.RoundToInt(render.x * scale), Mathf.RoundToInt(render.y * scale));
                    RectInt range = DesktopPetWindowGeometry.AllowedOrigins(work, visible, client, render);
                    float foot = range.yMax + (render.y - visible.yMin) * scale;
                    float left = range.xMin + visible.xMin * scale, right = range.xMax + visible.xMax * scale;
                    Require(Mathf.Abs(foot - (work.yMax - 2)) <= 1, "Feet float above the taskbar boundary.");
                    Require(left >= work.xMin + 1 && right <= work.xMax - 1, "Horizontal physical-pixel clamp mismatch.");
                    Require(range.width > 0 && range.height > 0, "The character cannot be dragged inside this monitor.");
                    count++;
                }
            report.AppendLine("PASS: " + count + " work-area/DPI combinations, including negative monitor origins; foot reaches work-area bottom within 2 physical pixels.");
            var tiny = new RectInt(0, 0, 800, 600);
            var large = new Rect(100, 40, 300, 700);
            var oversized = DesktopPetWindowGeometry.AllowedOrigins(tiny, large, render, render);
            Require(Mathf.Abs(oversized.yMax + render.y - large.yMin - (tiny.yMax - 2)) < 1, "Oversized panel pushes the feet under the taskbar.");
            report.AppendLine("PASS: oversized group keeps feet above the taskbar.");
            var appearance = UnityEngine.Object.FindAnyObjectByType<DesktopPetAppearance>();
            Require(appearance != null, "Missing DesktopPetAppearance.");
            appearance.RefreshPlacementBounds();
            Require(appearance.TryGetDesktopScreenBounds(out Rect actual), "Missing baked model footprint.");
            Require(actual.height > 10 && actual.width > 10 && actual.height < Screen.height * 3, "Invalid imported model footprint.");
            report.AppendLine("PASS: cached actual mesh screen bounds " + actual);
            report.Insert(0, "PASSED\n");
        } catch (Exception e) { report.Insert(0, "FAILED: " + e.Message + "\n"); Debug.LogError(e); }
        string path = Path.GetFullPath(Path.Combine(Application.dataPath, "../../../verification/advanced/desktop-boundary-regression.txt"));
        Directory.CreateDirectory(Path.GetDirectoryName(path)); File.WriteAllText(path, report.ToString()); Debug.Log(report);
    }
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
}

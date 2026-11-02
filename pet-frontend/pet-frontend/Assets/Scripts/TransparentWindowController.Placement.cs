using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;

public partial class TransparentWindowController
{
    public readonly struct ResidentMonitor
    {
        public string Id { get; }
        public string Label { get; }
        public ResidentMonitor(string id, string label) { Id = id; Label = label; }
    }

    private const string PlacementPrefsKey = "desktop_pet.window_placement";
    private readonly List<ResidentMonitor> residentMonitorOptions = new List<ResidentMonitor>();
    private string residentMonitorId = "";
    public IReadOnlyList<ResidentMonitor> ResidentMonitorOptions => residentMonitorOptions;
    public string ResidentMonitorId => residentMonitorId;
    public event Action MonitorPlacementChanged;
    public bool SupportsMonitorPlacement => NativeWindowHandle != IntPtr.Zero;

    /// <summary>Move the pet to the same relative resting position on another display.</summary>
    public bool SetResidentMonitor(string id)
    {
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
        using var dpi = new PhysicalPixels(true);
        if (window == IntPtr.Zero) return false;
        NativeMonitor target = FindNativeMonitor(id);
        if (target == null) return false;
        SavedPlacement current = CaptureNativePlacement(FindNativeMonitor(residentMonitorId));
        StopHandLead();
        character?.StopWalking();
        IsDragging = false;
        potentialDrag = false;
        ReleaseCapture();
        savePlacementAfterHandFall = false;
        if (!MoveToResidentMonitor(target, current)) return false;
        placementRecoveryPending = false;
        PersistNativePlacement(target);
        return true;
#else
        return false;
#endif
    }

#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
    [Serializable]
    private sealed class SavedPlacement
    {
        public int version = 1;
        public string monitorId;
        public int left, top;
        public int rangeLeft, rangeTop, rangeWidth, rangeHeight;
        public float normalizedX, normalizedY;
    }

    private sealed class NativeMonitor
    {
        public IntPtr handle;
        public string id;
        public RectInt workArea;
        public RectInt bounds;
        public bool primary;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        public int size;
        public Rect monitor, work;
        public uint flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string device;
    }
    private delegate bool EnumMonitorProc(IntPtr monitor, IntPtr dc, ref Rect bounds, IntPtr data);
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, EnumMonitorProc callback, IntPtr data);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern bool GetMonitorInfoEx(IntPtr monitor, ref MonitorInfoEx info);

    private List<NativeMonitor> connectedMonitors = new List<NativeMonitor>();
    private SavedPlacement savedPlacement;
    private float nextMonitorRefresh;
    private bool placementRecoveryPending;
    private bool savePlacementAfterHandFall;

    private void InitializeNativePlacement(Rect initialRect)
    {
        string json = PlayerPrefs.GetString(PlacementPrefsKey, "");
        if (!string.IsNullOrEmpty(json))
        {
            try
            {
                SavedPlacement stored = JsonUtility.FromJson<SavedPlacement>(json);
                if (stored != null && stored.version == 1 && !string.IsNullOrEmpty(stored.monitorId)
                    && !float.IsNaN(stored.normalizedX) && !float.IsInfinity(stored.normalizedX)
                    && !float.IsNaN(stored.normalizedY) && !float.IsInfinity(stored.normalizedY))
                    savedPlacement = stored;
            }
            catch (ArgumentException) { Debug.LogWarning("Stored desktop pet placement was invalid; using the primary display."); }
        }
        if (savedPlacement != null) residentMonitorId = savedPlacement.monitorId;
        RefreshConnectedMonitors();
        NativeMonitor monitor = FindNativeMonitor(residentMonitorId) ?? PrimaryNativeMonitor();
        if (monitor != null)
        {
            int left = Math.Max(monitor.workArea.xMin, monitor.workArea.xMax - (initialRect.right - initialRect.left));
            int top = Math.Max(monitor.workArea.yMin, monitor.workArea.yMax - (initialRect.bottom - initialRect.top));
            SetWindowPos(window, topmost ? new IntPtr(-1) : new IntPtr(-2), left, top, 0, 0,
                SWP_NOSIZE | SWP_NOACTIVATE | SWP_FRAMECHANGED);
        }
        else
            SetWindowPos(window, topmost ? new IntPtr(-1) : new IntPtr(-2), 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_FRAMECHANGED);
        // Appearance.Start restores model size after this controller's early Start.
        // LateUpdate restores the pet using the final visible bounds, without resizing it.
        placementRecoveryPending = true;
        nextMonitorRefresh = Time.unscaledTime + 1f;
    }

    private void UpdateNativePlacement()
    {
        if (window == IntPtr.Zero) return;
        if (Time.unscaledTime >= nextMonitorRefresh)
        {
            nextMonitorRefresh = Time.unscaledTime + 1f;
            SavedPlacement current = placementRecoveryPending ? savedPlacement : CaptureNativePlacement(FindNativeMonitor(residentMonitorId));
            if (RefreshConnectedMonitors())
            {
                if (current != null) savedPlacement = current;
                placementRecoveryPending = true;
            }
        }
        if (!placementRecoveryPending || IsDragging || handLead.Active || handFall.Active || DesktopPetPanelDrag.AnyDragging) return;
        NativeMonitor target = FindNativeMonitor(residentMonitorId) ?? PrimaryNativeMonitor();
        if (target == null || !MoveToResidentMonitor(target, savedPlacement)) return;
        placementRecoveryPending = false;
        PersistNativePlacement(target);
    }

    private bool RefreshConnectedMonitors()
    {
        var fresh = new List<NativeMonitor>();
        bool complete = EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero,
            (IntPtr handle, IntPtr dc, ref Rect bounds, IntPtr data) =>
            {
                var info = new MonitorInfoEx { size = Marshal.SizeOf<MonitorInfoEx>() };
                if (GetMonitorInfoEx(handle, ref info) && !string.IsNullOrEmpty(info.device))
                    fresh.Add(new NativeMonitor {
                        handle = handle, id = info.device, primary = (info.flags & 1) != 0,
                        workArea = new RectInt(info.work.left, info.work.top, info.work.right - info.work.left, info.work.bottom - info.work.top),
                        bounds = new RectInt(info.monitor.left, info.monitor.top, info.monitor.right - info.monitor.left, info.monitor.bottom - info.monitor.top)
                    });
                return true;
            }, IntPtr.Zero);
        if (!complete || fresh.Count == 0) return false;
        fresh.Sort((a, b) => {
            int primaryOrder = b.primary.CompareTo(a.primary);
            return primaryOrder != 0 ? primaryOrder : string.Compare(a.id, b.id, StringComparison.OrdinalIgnoreCase);
        });
        bool changed = fresh.Count != connectedMonitors.Count;
        for (int i = 0; !changed && i < fresh.Count; i++)
        {
            NativeMonitor previous = connectedMonitors[i], next = fresh[i];
            changed = previous.id != next.id || previous.primary != next.primary
                || previous.workArea != next.workArea || previous.bounds != next.bounds;
        }
        connectedMonitors = fresh;
        if (!changed) return false;
        residentMonitorOptions.Clear();
        for (int i = 0; i < fresh.Count; i++)
        {
            NativeMonitor item = fresh[i];
            int deviceIndex = item.id.LastIndexOf("DISPLAY", StringComparison.OrdinalIgnoreCase);
            string number = deviceIndex >= 0 ? item.id.Substring(deviceIndex + 7) : (i + 1).ToString();
            string label = $"显示器 {number}{(item.primary ? " · 主屏" : "")} · {item.bounds.width} × {item.bounds.height}";
            residentMonitorOptions.Add(new ResidentMonitor(item.id, label));
        }
        MonitorPlacementChanged?.Invoke();
        return true;
    }

    private NativeMonitor FindNativeMonitor(string id)
    {
        foreach (NativeMonitor item in connectedMonitors)
            if (string.Equals(item.id, id, StringComparison.OrdinalIgnoreCase)) return item;
        return null;
    }
    private NativeMonitor PrimaryNativeMonitor()
    {
        foreach (NativeMonitor item in connectedMonitors) if (item.primary) return item;
        return connectedMonitors.Count != 0 ? connectedMonitors[0] : null;
    }

    private IntPtr ResidentMonitorHandle()
    {
        NativeMonitor monitor = FindNativeMonitor(residentMonitorId);
        return monitor != null ? monitor.handle : MonitorFromWindow(window, 2);
    }

    private RectInt PlacementRange(NativeMonitor monitor)
    {
        // Use the cached work area when capturing before a topology refresh. Native
        // GetMonitorInfo already reflects the new layout at that point.
        if (TryGetAllowedOrigins(monitor.workArea, out RectInt range)) return range;
        GetWindowRect(window, out Rect current);
        int width = Math.Max(0, monitor.workArea.width - (current.right - current.left));
        int height = Math.Max(0, monitor.workArea.height - (current.bottom - current.top));
        return new RectInt(monitor.workArea.xMin, monitor.workArea.yMin, width, height);
    }

    private bool MoveToResidentMonitor(NativeMonitor monitor, SavedPlacement placement)
    {
        RectInt range = PlacementRange(monitor);
        int left = range.xMax, top = range.yMax;
        if (placement != null)
        {
            bool sameRange = range.xMin == placement.rangeLeft && range.yMin == placement.rangeTop
                && range.width == placement.rangeWidth && range.height == placement.rangeHeight;
            Vector2Int restored = DesktopPetWindowGeometry.RestoreOrigin(range, new Vector2(placement.normalizedX, placement.normalizedY));
            left = sameRange ? placement.left : restored.x;
            top = sameRange ? placement.top : restored.y;
            left = Mathf.Clamp(left, range.xMin, range.xMax);
            top = Mathf.Clamp(top, range.yMin, range.yMax);
        }
        if (!SetWindowPos(window, IntPtr.Zero, left, top, 0, 0, SWP_NOSIZE | SWP_NOACTIVATE | 4)) return false;
        residentMonitorId = monitor.id;
        return true;
    }

    private SavedPlacement CaptureNativePlacement(NativeMonitor monitor)
    {
        if (monitor == null || window == IntPtr.Zero || !GetWindowRect(window, out Rect current)) return null;
        RectInt range = PlacementRange(monitor);
        int top = handFall.Active || handLead.Active ? handGroundTop : current.top;
        int left = Mathf.Clamp(current.left, range.xMin, range.xMax);
        top = Mathf.Clamp(top, range.yMin, range.yMax);
        Vector2 normalized = DesktopPetWindowGeometry.NormalizeOrigin(range, new Vector2Int(left, top));
        return new SavedPlacement {
            monitorId = monitor.id, left = left, top = top,
            rangeLeft = range.xMin, rangeTop = range.yMin, rangeWidth = range.width, rangeHeight = range.height,
            normalizedX = normalized.x, normalizedY = normalized.y
        };
    }

    private void PersistNativePlacement(NativeMonitor monitor)
    {
        SavedPlacement current = CaptureNativePlacement(monitor);
        if (current == null) return;
        savedPlacement = current;
        bool changedMonitor = residentMonitorId != current.monitorId;
        residentMonitorId = current.monitorId;
        PlayerPrefs.SetString(PlacementPrefsKey, JsonUtility.ToJson(current));
        PlayerPrefs.Save();
        // Also refresh after an explicit selection even when MoveToResidentMonitor already set the id.
        if (changedMonitor || !placementRecoveryPending) MonitorPlacementChanged?.Invoke();
    }

    private void SaveNativePlacement()
    {
        if (window == IntPtr.Zero) return;
        IntPtr handle = VisiblePetMonitorHandle();
        NativeMonitor monitor = null;
        foreach (NativeMonitor item in connectedMonitors) if (item.handle == handle) { monitor = item; break; }
        PersistNativePlacement(monitor ?? FindNativeMonitor(residentMonitorId) ?? PrimaryNativeMonitor());
        savePlacementAfterHandFall = false;
    }

    private IntPtr VisiblePetMonitorHandle()
    {
        IntPtr handle = ResidentMonitorHandle();
        if (appearance != null && appearance.TryGetDesktopScreenBounds(out UnityEngine.Rect visible)
            && GetWindowRect(window, out Rect current) && GetClientRect(window, out Rect client))
        {
            var center = new Point {
                x = current.left + Mathf.RoundToInt(visible.center.x * (client.right - client.left) / Mathf.Max(1f, Screen.width)),
                y = current.top + Mathf.RoundToInt((Screen.height - visible.center.y) * (client.bottom - client.top) / Mathf.Max(1f, Screen.height))
            };
            handle = MonitorFromPoint(center, 2);
        }
        return handle;
    }

    private void RememberNativePlacementAfterHandLead()
    {
        IntPtr handle = VisiblePetMonitorHandle();
        foreach (NativeMonitor item in connectedMonitors)
            if (item.handle == handle) { residentMonitorId = item.id; break; }
        if (handFall.Active) savePlacementAfterHandFall = true;
        else SaveNativePlacement();
    }

    private void SaveNativePlacementAfterLanding()
    {
        if (savePlacementAfterHandFall) SaveNativePlacement();
    }
#endif

    /// <summary>Atomically exercises available displays, then restores the native rect and exact preference text.</summary>
    public void CheckNativePlacement(Action<string> fail)
    {
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
        using var dpi = new PhysicalPixels(true);
        if (window == IntPtr.Zero || !GetWindowRect(window, out Rect originalRect))
        { fail?.Invoke("Native placement: missing player window."); return; }
        if (IsDragging || handLead.Pending || handFall.Active || DesktopPetPanelDrag.AnyDragging || placementRecoveryPending)
        { fail?.Invoke("Native placement: wait for startup placement and active pointer gestures to finish."); return; }
        bool hadPreference = PlayerPrefs.HasKey(PlacementPrefsKey);
        string originalPreference = PlayerPrefs.GetString(PlacementPrefsKey, "");
        SavedPlacement originalSaved = savedPlacement;
        string originalMonitorId = residentMonitorId;
        List<NativeMonitor> originalMonitors = connectedMonitors;
        var originalOptions = new List<ResidentMonitor>(residentMonitorOptions);
        float originalNextRefresh = nextMonitorRefresh;
        bool originalRecoveryPending = placementRecoveryPending;
        bool originalSaveAfterFall = savePlacementAfterHandFall;
        try
        {
            RefreshConnectedMonitors();
            RequirePlacement(connectedMonitors.Count > 0 && residentMonitorOptions.Count == connectedMonitors.Count,
                "Display enumeration did not produce the same number of selectable displays.");
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int primaryCount = 0;
            foreach (NativeMonitor monitor in connectedMonitors)
            {
                RequirePlacement(ids.Add(monitor.id) && monitor.handle != IntPtr.Zero
                    && monitor.bounds.width > 0 && monitor.bounds.height > 0
                    && monitor.workArea.width > 0 && monitor.workArea.height > 0, "An enumerated display has an invalid id, handle or area.");
                RequirePlacement(monitor.workArea.xMin >= monitor.bounds.xMin && monitor.workArea.yMin >= monitor.bounds.yMin
                    && monitor.workArea.xMax <= monitor.bounds.xMax && monitor.workArea.yMax <= monitor.bounds.yMax,
                    "An enumerated display work area escapes its physical bounds.");
                if (monitor.primary) primaryCount++;
            }
            RequirePlacement(primaryCount == 1, "Display enumeration must identify one primary display.");
            NativeMonitor current = FindNativeMonitor(residentMonitorId);
            RequirePlacement(current != null, "The selected resident display is absent from the available displays.");
            var currentInfo = new MonitorInfoEx { size = System.Runtime.InteropServices.Marshal.SizeOf<MonitorInfoEx>() };
            RequirePlacement(GetMonitorInfoEx(ResidentMonitorHandle(), ref currentInfo) && currentInfo.device == current.id,
                "The resident display id does not match the selected native monitor handle.");
            SavedPlacement baseline = CaptureNativePlacement(current);
            RequirePlacement(baseline != null, "The current native position cannot be captured.");

            foreach (NativeMonitor target in connectedMonitors)
            {
                RequirePlacement(MoveToResidentMonitor(target, baseline), "The selected display cannot receive the pet window.");
                RequirePlacement(residentMonitorId == target.id && ResidentMonitorHandle() == target.handle,
                    "Changing displays leaves an inconsistent resident selection.");
                RectInt range = PlacementRange(target);
                Vector2Int sample = DesktopPetWindowGeometry.RestoreOrigin(range, new Vector2(.25f, .75f));
                RequirePlacement(SetWindowPos(window, IntPtr.Zero, sample.x, sample.y, 0, 0, SWP_NOSIZE | SWP_NOACTIVATE | 4),
                    "The current display cannot receive a test resting position.");
                PersistNativePlacement(target);
                SavedPlacement stored = JsonUtility.FromJson<SavedPlacement>(PlayerPrefs.GetString(PlacementPrefsKey));
                RequirePlacement(stored != null && stored.version == 1 && stored.monitorId == target.id,
                    "Saving position loses its resident display id.");
                RequirePlacement(GetWindowRect(window, out Rect savedRect) && savedRect.left == stored.left && savedRect.top == stored.top,
                    "Saved physical pixels differ from the current resting position.");
                Vector2Int other = DesktopPetWindowGeometry.RestoreOrigin(range, new Vector2(.75f, .25f));
                RequirePlacement(SetWindowPos(window, IntPtr.Zero, other.x, other.y, 0, 0, SWP_NOSIZE | SWP_NOACTIVATE | 4),
                    "The native window cannot move away from the saved position.");
                RequirePlacement(MoveToResidentMonitor(target, stored) && GetWindowRect(window, out Rect restored)
                    && restored.left == stored.left && restored.top == stored.top, "Save and restore changes the physical resting pixel.");
            }
        }
        catch (Exception exception) { fail?.Invoke("Native placement: " + exception.Message); }
        finally
        {
            savedPlacement = originalSaved;
            residentMonitorId = originalMonitorId;
            connectedMonitors = originalMonitors;
            residentMonitorOptions.Clear(); residentMonitorOptions.AddRange(originalOptions);
            nextMonitorRefresh = originalNextRefresh;
            placementRecoveryPending = originalRecoveryPending;
            savePlacementAfterHandFall = originalSaveAfterFall;
            bool restoredWindow = SetWindowPos(window, IntPtr.Zero, originalRect.left, originalRect.top,
                originalRect.right - originalRect.left, originalRect.bottom - originalRect.top, SWP_NOACTIVATE | 4);
            if (hadPreference) PlayerPrefs.SetString(PlacementPrefsKey, originalPreference);
            else PlayerPrefs.DeleteKey(PlacementPrefsKey);
            PlayerPrefs.Save();
            MonitorPlacementChanged?.Invoke();
            if (!restoredWindow || !GetWindowRect(window, out Rect after)
                || after.left != originalRect.left || after.top != originalRect.top
                || after.right != originalRect.right || after.bottom != originalRect.bottom)
                fail?.Invoke("Native placement: QA did not restore the original native window rectangle.");
            if (PlayerPrefs.HasKey(PlacementPrefsKey) != hadPreference
                || (hadPreference && PlayerPrefs.GetString(PlacementPrefsKey) != originalPreference))
                fail?.Invoke("Native placement: QA did not restore the original position preference.");
        }
#endif
    }

    private static void RequirePlacement(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

}

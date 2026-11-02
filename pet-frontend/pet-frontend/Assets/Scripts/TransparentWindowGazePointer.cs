using System;
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

public partial class TransparentWindowController
{
    /// <summary>Uses the same physical/client pixel conversion as desktop dragging.
    /// Values may lie outside the pet window so an unfocused pet can follow the cursor.</summary>
    public bool TryGetGazePointer(out Vector2 pointer)
    {
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
        using var dpi = new PhysicalPixels(true);
        if (window != IntPtr.Zero && GetCursorPos(out Point cursor) && ScreenToClient(window, ref cursor)
            && GetClientRect(window, out Rect clientRect))
        {
            pointer = new Vector2(cursor.x * Screen.width / (float)Math.Max(1, clientRect.right),
                (clientRect.bottom - cursor.y) * Screen.height / (float)Math.Max(1, clientRect.bottom));
            return true;
        }
#endif
        return TryGetLocalGazePointer(out pointer);
    }

    public static bool TryGetLocalGazePointer(out Vector2 pointer)
    {
#if ENABLE_INPUT_SYSTEM
        if (Mouse.current != null) { pointer = Mouse.current.position.ReadValue(); return true; }
#elif ENABLE_LEGACY_INPUT_MANAGER
        pointer = Input.mousePosition; return true;
#endif
        pointer = default; return false;
    }
}

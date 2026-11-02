using UnityEngine;

public partial class TransparentWindowController
{
    public bool SettingsWorkspaceOpen { get; private set; }

    /// <summary>Pause desktop movement while settings use the existing canvas.
    /// Opening a panel must not resize the native window or change the camera.</summary>
    public bool BeginSettingsWorkspace(Canvas canvas)
    {
        if (SettingsWorkspaceOpen) return true;
        SettingsWorkspaceOpen = true;
        character?.StopWalking();
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
        potentialDrag = false;
        ReleaseCapture();
#endif
        return true;
    }

    public void EndSettingsWorkspace() => SettingsWorkspaceOpen = false;
}
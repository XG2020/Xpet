using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

public class SettingsPanelController : MonoBehaviour
{
    [SerializeField] private GameObject panelRoot;
    [SerializeField] private bool openOnStart;
    public bool IsOpen => panelRoot != null && panelRoot.activeSelf;
    private void Start() { SetVisible(openOnStart); }

    private void Update()
    {
#if ENABLE_INPUT_SYSTEM
        if (Keyboard.current == null) return;
        if (IsOpen && Keyboard.current.escapeKey.wasPressedThisFrame) Close();
#elif ENABLE_LEGACY_INPUT_MANAGER
        if (IsOpen && Input.GetKeyDown(KeyCode.Escape)) Close();
#endif
    }
    public void Toggle() { SetVisible(!IsOpen); }
    public void Open() { SetVisible(true); }
    public void Close() { SetVisible(false); }
    private void SetVisible(bool visible)
    {
        if (panelRoot == null) return;
        panelRoot.SetActive(visible);
        if (visible) panelRoot.transform.SetAsLastSibling();
    }
}

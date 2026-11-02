using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>Installs gaze on existing and imported characters without rewriting model bundles.</summary>
[DisallowMultipleComponent]
public sealed class PetMouseGazeSettings : MonoBehaviour
{
    private PetModelLibrary library;
    private RectTransform controls;
    private UnityEngine.UI.Button[] buttons;
    private float retryAt;
    private static readonly PetMouseGazeMode[] Modes = { PetMouseGazeMode.Always, PetMouseGazeMode.Occasional, PetMouseGazeMode.Off };

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Install()
    {
        SceneManager.sceneLoaded -= SceneLoaded;
        SceneManager.sceneLoaded += SceneLoaded;
        InstallScene();
    }
    private static void SceneLoaded(Scene scene, LoadSceneMode mode) { InstallScene(); }
    private static void InstallScene()
    {
        var appearance = FindFirstObjectByType<DesktopPetAppearance>(FindObjectsInactive.Include);
        if (appearance != null && appearance.GetComponent<PetMouseGazeSettings>() == null) appearance.gameObject.AddComponent<PetMouseGazeSettings>();
        foreach (var character in FindObjectsByType<PetCharacterAnimation>(FindObjectsInactive.Include)) PetMouseGaze.InstallOn(character.gameObject);
    }
    private void OnEnable()
    {
        library = GetComponent<PetModelLibrary>();
        if (library != null) library.StatusChanged += InstallCharacters;
        PetMouseGaze.ModeChanged += Refresh;
        InstallCharacters();
    }
    private void OnDisable()
    {
        if (library != null) library.StatusChanged -= InstallCharacters;
        PetMouseGaze.ModeChanged -= Refresh;
    }
    private void Start() { TryBuild(); }
    private void Update()
    {
        if (controls != null || Time.unscaledTime < retryAt) return;
        retryAt = Time.unscaledTime + 1f; TryBuild();
    }
    private void InstallCharacters()
    {
        foreach (var character in FindObjectsByType<PetCharacterAnimation>(FindObjectsInactive.Include)) PetMouseGaze.InstallOn(character.gameObject);
    }
    private void TryBuild()
    {
        var tabs = FindFirstObjectByType<SettingsTabsController>(FindObjectsInactive.Include);
        var parent = tabs != null ? tabs.FindPageContent("外观") : null;
        if (parent == null) return;
        if (parent.Find("MouseGazeSettings") != null) return;
        var font = parent.GetComponentInChildren<TMP_Text>(true)?.font;
        var root = new GameObject("MouseGazeSettings", typeof(RectTransform), typeof(UnityEngine.UI.Image), typeof(UnityEngine.UI.VerticalLayoutGroup), typeof(UnityEngine.UI.LayoutElement));
        controls = (RectTransform)root.transform; controls.SetParent(parent, false);
        root.GetComponent<UnityEngine.UI.Image>().color = new Color32(24, 33, 48, 255);
        root.GetComponent<UnityEngine.UI.Image>().raycastTarget = false;
        var group = root.GetComponent<UnityEngine.UI.VerticalLayoutGroup>();
        group.spacing = 8; group.childControlWidth = group.childControlHeight = true;
        group.padding = new RectOffset(12,12,12,12);
        group.childForceExpandWidth = true; group.childForceExpandHeight = false;
        root.GetComponent<UnityEngine.UI.LayoutElement>().minHeight = root.GetComponent<UnityEngine.UI.LayoutElement>().preferredHeight = 142;
        Label(controls, "鼠标注视", 18, 25, font);
        Label(controls, "眼睛轻轻跟随，头部小幅转向；动作、拖拽和走路时暂停。", 13, 32, font);
        var row = new GameObject("Modes", typeof(RectTransform), typeof(UnityEngine.UI.HorizontalLayoutGroup), typeof(UnityEngine.UI.LayoutElement));
        row.transform.SetParent(controls, false);
        row.GetComponent<UnityEngine.UI.LayoutElement>().preferredHeight = 38;
        var horizontal = row.GetComponent<UnityEngine.UI.HorizontalLayoutGroup>(); horizontal.spacing = 8;
        horizontal.childControlWidth = horizontal.childControlHeight = true; horizontal.childForceExpandWidth = true;
        buttons = new UnityEngine.UI.Button[3];
        string[] titles = { "始终注视", "偶尔注视", "关闭" };
        for (int i = 0; i < 3; i++)
        {
            int index = i;
            var button = new GameObject(titles[i], typeof(RectTransform), typeof(UnityEngine.UI.Image), typeof(UnityEngine.UI.Button), typeof(UnityEngine.UI.LayoutElement));
            button.transform.SetParent(row.transform, false); button.GetComponent<UnityEngine.UI.LayoutElement>().flexibleWidth = 1;
            buttons[i] = button.GetComponent<UnityEngine.UI.Button>(); buttons[i].targetGraphic = button.GetComponent<UnityEngine.UI.Image>();
            buttons[i].onClick.AddListener(() => PetMouseGaze.SetMode(Modes[index]));
            var label = Label(button.transform, titles[i], 15, 0, font);
            label.alignment = TextAlignmentOptions.Center;
            var rect = label.rectTransform; rect.anchorMin = Vector2.zero; rect.anchorMax = Vector2.one; rect.offsetMin = rect.offsetMax = Vector2.zero;
        }
        Refresh();
        UnityEngine.UI.LayoutRebuilder.MarkLayoutForRebuild(parent);
    }
    private static TMP_Text Label(Transform parent, string value, int size, int height, TMP_FontAsset font)
    {
        var item = new GameObject("Label", typeof(RectTransform), typeof(TextMeshProUGUI)); item.transform.SetParent(parent, false);
        var text = item.GetComponent<TextMeshProUGUI>(); text.font = font; text.fontSize = size; text.text = value; text.color = Color.white;
        text.raycastTarget = false; text.richText = false;
        if (height > 0) { var layout = item.AddComponent<UnityEngine.UI.LayoutElement>(); layout.minHeight = layout.preferredHeight = height; }
        return text;
    }
    private void Refresh()
    {
        if (buttons == null) return;
        for (int i = 0; i < buttons.Length; i++)
        {
            bool selected = PetMouseGaze.Mode == Modes[i];
            buttons[i].targetGraphic.color = selected ? new Color32(147, 232, 205, 255) : new Color32(36, 47, 63, 255);
            buttons[i].GetComponentInChildren<TMP_Text>().color = selected ? new Color32(17, 48, 41, 255) : Color.white;
        }
    }
}

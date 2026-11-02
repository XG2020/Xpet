using System.Collections;
using System.Globalization;
using TMPro;
using UnityEngine;
using static PetSettingsWidgets;

/// <summary>Rest controls are local preferences; they never save provider configuration.</summary>
public sealed class PetBehaviorSettings : MonoBehaviour
{
    private PetBehaviorCoordinator behavior;
    private TMP_InputField delay;
    private TMP_Text status;
    private float nextRefresh;
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Install() { new GameObject("BehaviorSettingsInstaller").AddComponent<PetBehaviorSettingsInstaller>(); }
    public void Build(PetBehaviorCoordinator coordinator)
    {
        behavior = coordinator;
        Label(transform, "安静与休息", 19, 30);
        Label(transform, "长时间没有与宠物互动时安静休息。点击、唤醒词或新的指令会唤醒；聊天和设置操作期间不会自动睡眠。", 14, 56);
        var toggleObject = Box("AutoSleep", transform, Color.clear); Height(toggleObject, 36);
        var toggle = toggleObject.gameObject.AddComponent<UnityEngine.UI.Toggle>();
        var box = Box("Box", toggleObject, Field); box.anchorMin = box.anchorMax = new Vector2(0,.5f); box.pivot = new Vector2(0,.5f); box.sizeDelta = new Vector2(24,24);
        var mark = Box("Mark", box, Accent); Stretch(mark,4); toggle.targetGraphic = box.GetComponent<UnityEngine.UI.Image>(); toggle.graphic = mark.GetComponent<UnityEngine.UI.Image>();
        var title = Label(toggleObject, "自动休息", 15); Stretch(title.rectTransform); title.rectTransform.offsetMin = new Vector2(34,0);
        toggle.SetIsOnWithoutNotify(behavior.AutoSleep); toggle.onValueChanged.AddListener(behavior.SetAutoSleep);
        delay = Input(transform, "无互动多久后休息（分钟）", "0.5–120", 8);
        delay.contentType = TMP_InputField.ContentType.DecimalNumber; delay.SetTextWithoutNotify((behavior.SleepDelaySeconds / 60).ToString("0.##", CultureInfo.InvariantCulture));
        var row = Row("RestControls", transform);
        Button(row, "保存休息时间", Save, true);
        Button(row, "现在休息", () => { status.text = behavior.RequestSleep() ? "准备安静休息。" : "请先结束当前交互或回复。"; });
        Button(row, "唤醒", () => behavior.Wake());
        status = Label(transform, "", 14, 40);
    }
    private void Save()
    {
        if (!float.TryParse(delay.text, NumberStyles.Float, CultureInfo.InvariantCulture, out float minutes)
            || float.IsNaN(minutes) || float.IsInfinity(minutes) || minutes < .5f || minutes > 120) {
            status.text = "请填写 0.5 到 120 分钟。"; return;
        }
        behavior.SetSleepDelay(minutes * 60); status.text = "休息时间已保存。";
    }
    private void Update()
    {
        if (behavior == null || Time.unscaledTime < nextRefresh) return;
        nextRefresh = Time.unscaledTime + 1;
        if (behavior.IsSleeping) status.text = behavior.SleepPhase == PetSleepPhase.Waking ? "正在醒来。" : "正在安静休息。";
    }
}

public sealed class PetBehaviorSettingsInstaller : MonoBehaviour
{
    private IEnumerator Start()
    {
        for (int i = 0; i < 120; i++) {
            var client = FindAnyObjectByType<PetBackendHttpClient>();
            var tabs = FindAnyObjectByType<SettingsTabsController>(FindObjectsInactive.Include);
            var content = tabs?.FindPageContent("外观");
            if (client != null && content != null) {
                if (content.Find("BehaviorSettings") == null) {
                    var group = Rect("BehaviorSettings", content); Vertical(group);
                    group.gameObject.AddComponent<PetBehaviorSettings>().Build(PetBehaviorCoordinator.EnsureOn(client.gameObject));
                }
                Destroy(gameObject); yield break;
            }
            yield return null;
        }
        Destroy(gameObject);
    }
}

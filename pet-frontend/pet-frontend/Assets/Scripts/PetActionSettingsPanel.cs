using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using TMPro;
using UnityEngine;

/// <summary>Manage built-ins, reusable combinations and locally imported VMD bone motions.</summary>
public sealed class PetActionSettingsPanel : MonoBehaviour
{
    [SerializeField] private TMP_Dropdown actionDropdown;
    [SerializeField] private UnityEngine.UI.Toggle enabledToggle;
    [SerializeField] private UnityEngine.UI.Button previewButton;
    [SerializeField] private UnityEngine.UI.Button deleteButton;
    [SerializeField] private TMP_Text detailsText;
    [SerializeField] private TMP_Text statusText;
    [SerializeField] private TMP_InputField nameInput;
    [SerializeField] private TMP_Dropdown stepDropdown;
    [SerializeField] private TMP_InputField secondsInput;
    [SerializeField] private UnityEngine.UI.Button addStepButton;
    [SerializeField] private UnityEngine.UI.Button removeStepButton;
    [SerializeField] private TMP_Text draftText;
    [SerializeField] private TMP_InputField speedInput;
    [SerializeField] private TMP_Dropdown triggerDropdown;
    [SerializeField] private UnityEngine.UI.Button createButton;
    [SerializeField] private TMP_InputField importNameInput;
    [SerializeField] private TMP_InputField importPathInput;
    [SerializeField] private TMP_InputField importSpeedInput;
    [SerializeField] private TMP_Dropdown importTriggerDropdown;
    [SerializeField] private UnityEngine.UI.Button pickMotionButton;
    [SerializeField] private UnityEngine.UI.Button importMotionButton;
    [SerializeField] private TMP_Dropdown selectedTriggerDropdown;
    [SerializeField] private UnityEngine.UI.Button saveTriggerButton;
    private bool importing;
    private readonly List<PetActionStep> draft = new List<PetActionStep>();
    private string[] ids = Array.Empty<string>();
    private string[] stepIds = Array.Empty<string>();
    private string[] triggerIds = Array.Empty<string>();
    private string selectedId;

    private void OnEnable()
    {
        PetActionLibrary.Changed += Refresh;
        actionDropdown.onValueChanged.AddListener(Select);
        enabledToggle.onValueChanged.AddListener(SetEnabled);
        previewButton.onClick.AddListener(Preview);
        deleteButton.onClick.AddListener(Delete);
        addStepButton.onClick.AddListener(AddStep);
        removeStepButton.onClick.AddListener(RemoveStep);
        createButton.onClick.AddListener(Create);
        if (pickMotionButton != null) pickMotionButton.onClick.AddListener(PickMotion);
        if (importMotionButton != null) importMotionButton.onClick.AddListener(ImportMotion);
        PetImportedMotionPlayer.PlaybackError += SetStatus;
        if (saveTriggerButton != null) saveTriggerButton.onClick.AddListener(SaveTrigger);
        stepIds = PetActionLibrary.BuiltInIds.Where(PetActionLibrary.CanCompose).ToArray();
        stepDropdown.ClearOptions(); stepDropdown.AddOptions(stepIds.Select(PetActionLibrary.Label).ToList());
        triggerIds = new[] { "", "ai_decide" }.Concat(stepIds.Where(id => id != "idle")).ToArray();
        triggerDropdown.ClearOptions(); triggerDropdown.AddOptions(triggerIds.Select(TriggerLabel).ToList());
        if (selectedTriggerDropdown != null) { selectedTriggerDropdown.ClearOptions(); selectedTriggerDropdown.AddOptions(triggerIds.Select(TriggerLabel).ToList()); }
        if (importTriggerDropdown != null) { importTriggerDropdown.ClearOptions(); importTriggerDropdown.AddOptions(triggerIds.Select(TriggerLabel).ToList()); }
        if (importSpeedInput != null && string.IsNullOrWhiteSpace(importSpeedInput.text)) importSpeedInput.SetTextWithoutNotify("1");
        if (string.IsNullOrWhiteSpace(secondsInput.text)) secondsInput.SetTextWithoutNotify("2");
        if (string.IsNullOrWhiteSpace(speedInput.text)) speedInput.SetTextWithoutNotify("1");
        Refresh(); RefreshDraft();
    }

    private void OnDisable()
    {
        StopAllCoroutines(); importing = false;
        if (pickMotionButton != null) pickMotionButton.interactable = true;
        if (importMotionButton != null) importMotionButton.interactable = true;
        PetActionLibrary.Changed -= Refresh;
        actionDropdown.onValueChanged.RemoveListener(Select);
        enabledToggle.onValueChanged.RemoveListener(SetEnabled);
        previewButton.onClick.RemoveListener(Preview);
        deleteButton.onClick.RemoveListener(Delete);
        addStepButton.onClick.RemoveListener(AddStep);
        removeStepButton.onClick.RemoveListener(RemoveStep);
        createButton.onClick.RemoveListener(Create);
        if (pickMotionButton != null) pickMotionButton.onClick.RemoveListener(PickMotion);
        if (importMotionButton != null) importMotionButton.onClick.RemoveListener(ImportMotion);
        PetImportedMotionPlayer.PlaybackError -= SetStatus;
        if (saveTriggerButton != null) saveTriggerButton.onClick.RemoveListener(SaveTrigger);
    }

    public void Refresh()
    {
        ids = PetActionLibrary.Entries.Select(entry => entry.id).ToArray();
        actionDropdown.ClearOptions();
        actionDropdown.AddOptions(PetActionLibrary.Entries.Select(entry => (entry.enabled ? "已启用 · " : "已关闭 · ") + (entry.builtIn ? "内置 · " : PetActionLibrary.IsImported(entry) ? "导入 · " : "组合 · ") + entry.label).ToList());
        int index = Mathf.Max(0, Array.IndexOf(ids, selectedId));
        actionDropdown.SetValueWithoutNotify(index); actionDropdown.RefreshShownValue(); Select(index);
    }

    private void Select(int index)
    {
        if (index < 0 || index >= ids.Length) return;
        selectedId = ids[index];
        var entry = PetActionLibrary.Find(selectedId);
        enabledToggle.SetIsOnWithoutNotify(entry.enabled);
        deleteButton.interactable = !entry.builtIn;
        previewButton.interactable = entry.enabled && entry.id != "drag";
        if (selectedTriggerDropdown != null) { selectedTriggerDropdown.interactable = !entry.builtIn; selectedTriggerDropdown.SetValueWithoutNotify(Mathf.Max(0, Array.IndexOf(triggerIds, entry.ai_decide ? "ai_decide" : entry.trigger))); selectedTriggerDropdown.RefreshShownValue(); }
        if (saveTriggerButton != null) saveTriggerButton.interactable = !entry.builtIn;
        detailsText.text = entry.builtIn ? "内置默认动作 · 可开关，不可删除。" + (entry.id == "idle" ? "关闭后保持静止站姿。" : entry.id == "drag" ? "拖动人物即可体验；关闭后仍可移动人物。" : "")
            : (PetActionLibrary.IsImported(entry) ? "VMD 骨骼动作 · " + entry.duration.ToString("0.#", CultureInfo.InvariantCulture) + "秒（原速）" : string.Join(" → ", entry.steps.Select(step => PetActionLibrary.Label(step.action) + " " + step.seconds.ToString("0.#", CultureInfo.InvariantCulture) + "秒")))
              + "\n" + entry.speed.ToString("0.#", CultureInfo.InvariantCulture) + " 倍速 · " + (entry.ai_decide ? "由大模型自行决定" : string.IsNullOrEmpty(entry.trigger) ? "仅手动预览" : "跟随“" + PetActionLibrary.Label(entry.trigger) + "”自动触发");
    }

    private void SetEnabled(bool value) { Run(() => { PetActionLibrary.SetEnabled(selectedId, value); SetStatus(value ? "动作已启用并保存。" : "动作已关闭并保存，正在播放的此动作也会停止。"); }); }
    private void SaveTrigger() { Run(() => { string trigger = triggerIds[selectedTriggerDropdown.value]; PetActionLibrary.SetTrigger(selectedId, trigger == "ai_decide" ? "" : trigger, trigger == "ai_decide"); SetStatus("自动触发方式已保存。由模型决定的动作会加入当前角色可调用目录。"); }); }
    private void Preview()
    {
        var bridge = FindFirstObjectByType<PetAnimatorBridge>();
        if (bridge == null) { SetStatus("当前没有可播放动作的角色，请先切换至可用模型。"); return; }
        bridge.PreviewAction(selectedId); SetStatus("正在预览“" + PetActionLibrary.Label(selectedId) + "”。拖动人物或新的回应会结束组合预览。");
    }
    private void Delete() { Run(() => { PetActionLibrary.Delete(selectedId); selectedId = null; Refresh(); SetStatus("自定义动作已删除。内置动作保留。"); }); }
    private void AddStep()
    {
        if (draft.Count >= 8) { SetStatus("一个组合最多 8 步，请先移除最后一步。"); return; }
        if (!Number(secondsInput.text, out float seconds) || seconds < 0.5f || seconds > 10) { SetStatus("每步时长请填写 0.5～10 秒，例如 2.5。"); return; }
        draft.Add(new PetActionStep { action = stepIds[stepDropdown.value], seconds = seconds }); RefreshDraft(); SetStatus("步骤已加入草稿，填写名称后点击“保存新动作”。");
    }
    private void RemoveStep() { if (draft.Count > 0) draft.RemoveAt(draft.Count - 1); RefreshDraft(); }
    private void RefreshDraft()
    {
        draftText.text = draft.Count == 0 ? "尚未添加步骤。例：挥手 2 秒 → 点头 1.5 秒。" : string.Join(" → ", draft.Select((step, index) => (index + 1) + ". " + PetActionLibrary.Label(step.action) + " " + step.seconds.ToString("0.#", CultureInfo.InvariantCulture) + "秒"));
        removeStepButton.interactable = draft.Count > 0; addStepButton.interactable = draft.Count < 8;
    }
    private void Create()
    {
        Run(() =>
        {
            if (!Number(speedInput.text, out float speed)) throw new ArgumentException("播放速度请填写 0.5～2，例如 1。");
            string trigger = triggerIds[triggerDropdown.value];
            var entry = PetActionLibrary.Add(nameInput.text, draft, speed, trigger == "ai_decide" ? "" : trigger, trigger == "ai_decide");
            selectedId = entry.id; draft.Clear(); nameInput.SetTextWithoutNotify(""); Refresh(); RefreshDraft();
            SetStatus("新动作已保存并启用，可点击“预览动作”。关闭的基础动作会从组合中跳过。");
        });
    }
    private void PickMotion()
    {
        Run(() => { string path = PetMotionFileDialog.PickMotion(); if (string.IsNullOrEmpty(path)) return;
            importPathInput.SetTextWithoutNotify(path);
            if (string.IsNullOrWhiteSpace(importNameInput.text)) importNameInput.SetTextWithoutNotify(System.IO.Path.GetFileNameWithoutExtension(path).Substring(0, Mathf.Min(24, System.IO.Path.GetFileNameWithoutExtension(path).Length)));
        });
    }
    private void ImportMotion() { if (!importing) StartCoroutine(ImportRoutine()); }
    private IEnumerator ImportRoutine()
    {
        var cards = FindFirstObjectByType<PetCharacterCards>();
        string expectedCharacter = cards != null ? cards.ActiveId : "default";
        string label = importNameInput.text.Trim(), trigger = triggerIds[importTriggerDropdown.value];
        if (!Number(importSpeedInput.text, out float speed) || speed < 0.5f || speed > 2) { SetStatus("播放速度请填写 0.5～2 倍。"); yield break; }
        if (label.Length == 0 || label.Length > 24) { SetStatus("导入动作名称请填写 1～24 个字。"); yield break; }
        var task = PetImportedMotionPlayer.Prepare(importPathInput.text.Trim());
        importing = true; pickMotionButton.interactable = importMotionButton.interactable = false;
        SetStatus("正在读取并检查动作文件…");
        while (!task.IsCompleted) yield return null;
        importing = false; pickMotionButton.interactable = importMotionButton.interactable = true;
        if (cards != null && (cards.ActiveId != expectedCharacter || cards.IsBusy)) { SetStatus("角色已切换；动作未加入新角色，请重新导入。"); yield break; }
        if (task.IsFaulted) { SetStatus("导入失败：" + task.Exception.GetBaseException().Message); yield break; }
        Run(() =>
        {
            var entry = PetActionLibrary.AddImported(label, task.Result.fileName, task.Result.motion.Duration, speed, trigger == "ai_decide" ? "" : trigger, trigger == "ai_decide");
            selectedId = entry.id; Refresh(); SetStatus("动作已复制到本机动作库并保存；可预览、开关或删除。请使用与当前角色骨架兼容的 VMD。");
        });
    }
    private static bool Number(string text, out float value) => float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && !float.IsNaN(value) && !float.IsInfinity(value);
    private static string TriggerLabel(string id) => id.Length == 0 ? "仅手动预览" : id == "ai_decide" ? "由大模型自行决定" : "收到“" + PetActionLibrary.Label(id) + "”时播放";
    private void Run(Action action) { try { action(); } catch (Exception error) { SetStatus(error.Message); } }
    private void SetStatus(string value) { statusText.text = value; }
}

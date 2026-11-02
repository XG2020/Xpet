using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

[Serializable]
public sealed class PetActionStep
{
    public string action;
    public float seconds = 2;
}

[Serializable]
public sealed class PetActionEntry
{
    public string id;
    public string label;
    public bool enabled = true;
    public bool builtIn;
    public float speed = 1;
    public string trigger = "";
    public bool ai_decide;
    public PetActionStep[] steps = Array.Empty<PetActionStep>();
    public string motion_file = "";
    public string source_format = "";
    public float duration;
}

/// <summary>Local action preferences; built-ins are reconstructed, never removed by saved data.</summary>
public static class PetActionLibrary
{
    public const string PrefsKey = "desktop_pet.actions.v1";
    public const int MaximumCustomActions = 64;
    public static readonly string[] BuiltInIds = { "idle", "wave", "happy", "think", "sad", "surprised", "speak", "walk", "nod", "tilt_head", "shy", "stretch", "cheer", "look_around", "guard", "drag", "jump", "run", "leg_retract", "leg_shift", "leg_sway", "angry_stomp" };
    public static readonly string[] BuiltInLabels = { "自然待机", "挥手", "开心", "思考", "难过", "惊讶", "说话", "走路", "点头", "歪头", "害羞", "伸懒腰", "庆祝", "环顾", "后退遮挡", "拖拽", "跳跃", "跑步", "收腿避让", "轻移重心", "小幅摆腿", "生气跺脚" };
    [Serializable] private sealed class SavedData { public int version = 1; public PetActionEntry[] entries; }
    private static List<PetActionEntry> entries;
    // Keep old records in storage/export without exposing them to UI or playback.
    private static List<PetActionEntry> retiredEntries;
    private static int CustomActionCount => entries.Count(item => !item.builtIn) + retiredEntries.Count(item => item.id.StartsWith("custom_", StringComparison.Ordinal));
    private static PetActionEntry[] StoredEntries() { EnsureLoaded(); return entries.Concat(retiredEntries).ToArray(); }
    private static string characterId = "default";
    private static string StorageKey => characterId == "default" ? PrefsKey : PrefsKey + "." + characterId;
    public static event Action Changed;
    public static string CharacterId => characterId;
    public static IReadOnlyList<PetActionEntry> Entries { get { EnsureLoaded(); return entries; } }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetSession() { entries = null; retiredEntries = null; characterId = "default"; Changed = null; }

    public static PetActionEntry[] Export() => JsonUtility.FromJson<SavedData>(JsonUtility.ToJson(new SavedData { entries = StoredEntries() })).entries;

    public static void ApplyCharacter(string id, PetActionEntry[] actions)
    {
        characterId = string.IsNullOrWhiteSpace(id) ? "default" : id;
        PlayerPrefs.SetString(StorageKey, JsonUtility.ToJson(new SavedData { entries = actions ?? Array.Empty<PetActionEntry>() }));
        Reload();
        PlayerPrefs.Save();
    }

    public static string Normalize(string id) => string.IsNullOrWhiteSpace(id) ? "idle" : id.Trim().ToLowerInvariant();
    public static bool IsBuiltIn(string id) => Array.IndexOf(BuiltInIds, Normalize(id)) >= 0;
    public static bool IsRetiredBuiltIn(string id) => Normalize(id) == "hold_sword" || Normalize(id) == "draw_slash";
    private static bool UsesRetiredBuiltIn(PetActionEntry entry) => IsRetiredBuiltIn(entry.id) || IsRetiredBuiltIn(entry.trigger)
        || (entry.steps ?? Array.Empty<PetActionStep>()).Any(step => step != null && IsRetiredBuiltIn(step.action));
    public static PetActionEntry Find(string id) { EnsureLoaded(); string key = Normalize(id); return entries.Find(item => item.id == key); }
    public static bool IsEnabled(string id) => Find(id)?.enabled ?? false;
    public static string Label(string id) => Find(id)?.label ?? id;
    public static bool CanCompose(string id) => IsBuiltIn(id) && Normalize(id) != "drag" && Normalize(id) != "guard";

    public static PetActionEntry ResolveCustom(string requested, bool allowTrigger = true)
    {
        EnsureLoaded();
        string key = Normalize(requested);
        var direct = entries.Find(item => !item.builtIn && item.id == key);
        if (direct != null) return direct.enabled ? direct : null;
        // Disabling a built-in also disables its automatic substitution.
        return allowTrigger && IsEnabled(key) ? entries.Find(item => !item.builtIn && item.enabled && item.trigger == key) : null;
    }

    public static void SetEnabled(string id, bool enabled)
    {
        var entry = Find(id);
        if (entry == null) throw new ArgumentException("动作不存在。");
        entry.enabled = enabled;
        Save();
    }

    public static void SetTrigger(string id, string trigger, bool aiDecide)
    {
        var entry = Find(id);
        if (entry == null || entry.builtIn) throw new ArgumentException("请先选择自定义组合或导入动作。");
        trigger = string.IsNullOrEmpty(trigger) ? "" : Normalize(trigger);
        if (trigger.Length > 0 && StoredEntries().Any(item => item != entry && !item.builtIn && item.trigger == trigger)) throw new ArgumentException("这个固定触发已有动作，请选择其他触发方式。");
        string previous = entry.trigger; bool previousAi = entry.ai_decide;
        entry.trigger = trigger; entry.ai_decide = aiDecide;
        try { Validate(entry); } catch { entry.trigger = previous; entry.ai_decide = previousAi; throw; }
        Save();
    }

    public static void Delete(string id)
    {
        var entry = Find(id);
        if (entry == null) throw new ArgumentException("动作不存在。");
        if (entry.builtIn) throw new InvalidOperationException("内置默认动作不可删除，可以关闭。");
        entries.Remove(entry);
        Save();
    }

    public static PetActionEntry Add(string label, IEnumerable<PetActionStep> steps, float speed, string trigger = "", bool aiDecide = false)
    {
        EnsureLoaded();
        if (CustomActionCount >= MaximumCustomActions) throw new ArgumentException("最多保存 64 个自定义动作，请先删除不再使用的动作。");
        var entry = new PetActionEntry { id = "custom_" + Guid.NewGuid().ToString("N"), label = label?.Trim(), speed = speed,
            trigger = string.IsNullOrWhiteSpace(trigger) ? "" : Normalize(trigger), ai_decide = aiDecide, steps = steps?.Select(step => new PetActionStep { action = Normalize(step.action), seconds = step.seconds }).ToArray() };
        Validate(entry);
        if (entries.Any(item => string.Equals(item.label, entry.label, StringComparison.OrdinalIgnoreCase))) throw new ArgumentException("动作名称已存在，请换一个名称。");
        if (entry.trigger.Length > 0 && StoredEntries().Any(item => !item.builtIn && item.trigger == entry.trigger)) throw new ArgumentException("此触发动作已有自定义组合，请选择其他触发或仅手动预览。");
        entries.Add(entry);
        Save();
        return entry;
    }

    public static bool IsImported(PetActionEntry entry) => entry != null && !string.IsNullOrEmpty(entry.motion_file);

    public static PetActionEntry AddImported(string label, string fileName, float duration, float speed, string trigger = "", bool aiDecide = false)
    {
        EnsureLoaded();
        if (CustomActionCount >= MaximumCustomActions) throw new ArgumentException("最多保存 64 个自定义动作，请先删除不再使用的动作。");
        var entry = new PetActionEntry { id = "custom_" + Guid.NewGuid().ToString("N"), label = label?.Trim(), speed = speed,
            trigger = string.IsNullOrWhiteSpace(trigger) ? "" : Normalize(trigger), ai_decide = aiDecide, motion_file = fileName, source_format = "vmd", duration = duration };
        Validate(entry);
        if (entries.Any(item => string.Equals(item.label, entry.label, StringComparison.OrdinalIgnoreCase))) throw new ArgumentException("动作名称已存在，请换一个名称。");
        if (entry.trigger.Length > 0 && StoredEntries().Any(item => !item.builtIn && item.trigger == entry.trigger)) throw new ArgumentException("此触发动作已有绑定，请选择其他触发或仅手动预览。");
        entries.Add(entry); Save(); return entry;
    }

    private static void Validate(PetActionEntry entry, bool allowRetired = false)
    {
        if (entry.ai_decide && !string.IsNullOrEmpty(entry.trigger)) throw new ArgumentException("由大模型自行决定与固定动作触发只能选择一种。");
        if (string.IsNullOrWhiteSpace(entry.label) || entry.label.Length > 24 || entry.label.Any(char.IsControl)) throw new ArgumentException("动作名称请填写 1～24 个字，不含换行。");
        if (!Finite(entry.speed) || entry.speed < 0.5f || entry.speed > 2) throw new ArgumentException("播放速度须为 0.5～2 倍。");
        if (IsImported(entry))
        {
            if (entry.source_format != "vmd" || !System.Text.RegularExpressions.Regex.IsMatch(entry.motion_file, "^[a-f0-9]{64}\\.vmd$")
                || !Finite(entry.duration) || entry.duration <= 0 || entry.duration > 180 || (entry.steps != null && entry.steps.Length > 0))
                throw new ArgumentException("导入动作元数据无效，请重新导入 VMD 文件。");
        }
        else if (entry.steps == null || entry.steps.Length < 1 || entry.steps.Length > 8) throw new ArgumentException("每个组合需要 1～8 个动作步骤。");
        foreach (var step in entry.steps ?? Array.Empty<PetActionStep>())
            if (step == null || !(CanCompose(step.action) || allowRetired && IsRetiredBuiltIn(step.action)) || !Finite(step.seconds) || step.seconds < 0.5f || step.seconds > 10) throw new ArgumentException("步骤须选择内置回应动作，时长为 0.5～10 秒；拖拽和后退遮挡由交互单独控制。");
        if (!string.IsNullOrEmpty(entry.trigger) && (!(CanCompose(entry.trigger) || allowRetired && IsRetiredBuiltIn(entry.trigger)) || entry.trigger == "idle" || entry.trigger == "guard"))
            throw new ArgumentException("自动触发只能选择回应动作，不能替换待机、拖拽或后退遮挡。");
    }

    private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    private static List<PetActionEntry> Defaults() => BuiltInIds.Select((id, index) => new PetActionEntry { id = id, label = BuiltInLabels[index], builtIn = true }).ToList();

    private static void EnsureLoaded()
    {
        if (entries != null) return;
        entries = Defaults();
        retiredEntries = new List<PetActionEntry>();
        string json = PlayerPrefs.GetString(StorageKey, "");
        if (string.IsNullOrEmpty(json)) return;
        try
        {
            var saved = JsonUtility.FromJson<SavedData>(json);
            if (saved == null || saved.version != 1 || saved.entries == null) return;
            foreach (var item in saved.entries)
            {
                if (item == null || string.IsNullOrWhiteSpace(item.id)) continue;
                if (IsRetiredBuiltIn(item.id)) { retiredEntries.Add(item); continue; }
                var builtin = entries.Find(entry => entry.builtIn && entry.id == item.id);
                if (builtin != null) { builtin.enabled = item.enabled; continue; }
                if (!item.id.StartsWith("custom_", StringComparison.Ordinal) || entries.Any(entry => entry.id == item.id)
                    || retiredEntries.Any(entry => entry.id == item.id) || CustomActionCount >= MaximumCustomActions) continue;
                item.trigger = item.trigger ?? "";
                try { Validate(item, true); } catch (ArgumentException) { continue; }
                if (UsesRetiredBuiltIn(item)) { retiredEntries.Add(item); continue; }
                item.builtIn = false;
                if (item.trigger.Length > 0 && entries.Any(entry => !entry.builtIn && entry.trigger == item.trigger)) item.trigger = "";
                entries.Add(item);
            }
        }
        catch (Exception error) { Debug.LogWarning("动作偏好无法读取，使用内置默认动作：" + error.Message); }
    }

    private static void Save()
    {
        PlayerPrefs.SetString(StorageKey, JsonUtility.ToJson(new SavedData { entries = StoredEntries() }));
        PlayerPrefs.Save();
        Changed?.Invoke();
    }

    /// <summary>Re-read preferences after a settings migration or isolated regression.</summary>
    public static void Reload() { entries = null; EnsureLoaded(); Changed?.Invoke(); }
}

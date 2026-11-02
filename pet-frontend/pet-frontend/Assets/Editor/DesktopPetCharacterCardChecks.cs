using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;

/// <summary>Play-mode card regression against an explicitly configured, disposable backend only.</summary>
public static class DesktopPetCharacterCardChecks
{
    private const string TestUrl = "http://127.0.0.1:18081";
    private static PetCharacterCards cards;
    private static PetModelLibrary models;
    private static BackendSettingsApi api;
    private static PetBackendHttpClient chat;
    private static bool running;
    private static bool originalSpeech;
    private static string failure, originalUrl, originalScope, originalModel, originalId, directory;
    private static PetActionEntry[] originalActions;
    private static PetCharacterCard originalCard;
    private static readonly List<string> createdIds = new List<string>();
    private static readonly Dictionary<string, string> savedPrefs = new Dictionary<string, string>();
    private static readonly StringBuilder report = new StringBuilder();

    [MenuItem("Desktop Pet/Run Character Card Checks (Isolated Backend)")]
    public static void Run()
    {
        if (!EditorApplication.isPlaying) throw new InvalidOperationException("Enter Play Mode first.");
        if (running) throw new InvalidOperationException("Character-card regression is already running.");
        RequireSafeUrl();
        cards = UnityEngine.Object.FindAnyObjectByType<PetCharacterCards>();
        models = UnityEngine.Object.FindAnyObjectByType<PetModelLibrary>();
        api = UnityEngine.Object.FindAnyObjectByType<BackendSettingsApi>();
        chat = UnityEngine.Object.FindAnyObjectByType<PetBackendHttpClient>();
        Require(cards != null && models != null && api != null && chat != null, "Missing character-card services.");
        Require(cards.Ready && !cards.IsBusy && !models.IsBusy && !chat.IsBusy && !chat.IsSpeaking,
            "Wait for card/model loading and conversations to finish.");
        originalCard = Clone(cards.Cards.FirstOrDefault(c => c.id == cards.ActiveId));
        Require(originalCard != null, "The current card is absent from the card list.");
        originalId = cards.ActiveId;
        originalSpeech = chat.SpeechEnabled;
        originalUrl = api.BackendBaseUrl;
        originalModel = models.SelectedModelId;
        originalActions = PetActionLibrary.Export();
        originalScope = (string)typeof(PetActionLibrary).GetField("characterId", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
        savedPrefs.Clear(); createdIds.Clear(); report.Clear(); failure = null;
        Remember(DesktopPetPrefs.BackendBaseUrlPrefsKey);
        Remember(PetModelLibrary.SelectedModelKey);
        Remember(PetActionLibrary.PrefsKey);
        Remember(ActionKey(originalScope));
        directory = Path.GetFullPath(Path.Combine(Application.dataPath, "../../../verification/characters"));
        Directory.CreateDirectory(directory);
        running = true;
        EditorApplication.playModeStateChanged += OnPlayMode;
        cards.StartCoroutine(RunSafe());
    }

    private static IEnumerator RunSafe()
    {
        yield return Execute(Check(), message => failure = message);
        yield return Execute(Restore(), message => failure = (failure == null ? "" : failure + "\n") + "Restore: " + message);
        RestoreLocal();
        Finish();
    }

    // Flatten nested enumerators so assertions after asynchronous requests are
    // caught and restoration runs even when a nested test step fails.
    private static IEnumerator Execute(IEnumerator operation, Action<string> onFailure)
    {
        var stack = new Stack<IEnumerator>(); stack.Push(operation);
        while (stack.Count > 0)
        {
            bool moved = false; object current = null; Exception error = null;
            try { moved = stack.Peek().MoveNext(); if (moved) current = stack.Peek().Current; }
            catch (Exception caught) { error = caught.GetBaseException(); }
            if (error != null)
            {
                foreach (IEnumerator item in stack) (item as IDisposable)?.Dispose();
                onFailure(error.Message); yield break;
            }
            if (!moved) { (stack.Pop() as IDisposable)?.Dispose(); continue; }
            if (current is IEnumerator nested) stack.Push(nested);
            else yield return current;
        }
    }

    private static IEnumerator Check()
    {
        RuntimeSettingsData settings = null;
        yield return Get<RuntimeSettingsData>("/api/settings", value => settings = value);
        Require(settings != null && (settings.llm_provider == "offline" || settings.llm_provider == "mock")
            && !settings.enable_proactive_screen && !settings.enable_real_rpa,
            "The disposable backend must be offline/mock, with proactive screen and real RPA disabled.");
        var secondModel = models.ModelEntries.FirstOrDefault(m => m.id != PetModelLibrary.BuiltinModelId);
        Require(secondModel != null, "Install one test model to verify actual model switching.");
        string suffix = Guid.NewGuid().ToString("N").Substring(0, 8);
        var a = NewCard("QA-A-" + suffix, PetModelLibrary.BuiltinModelId, false, true, "zh-CN-XiaoxiaoNeural");
        var b = NewCard("QA-B-" + suffix, secondModel.id, true, false, "zh-CN-YunxiNeural");
        yield return Create(a, value => a = value);
        yield return Create(b, value => b = value);
        report.AppendLine("PASS: created two separate character cards with distinct models, identity, voice and action states.");

        cards.Activate(a); yield return WaitIdle();
        yield return AssertActive(a, false, true);
        // Switch immediately, inside the 0.7-second action debounce window.
        // The change must survive returning to A, not merely exist in memory.
        PetActionLibrary.SetEnabled("wave", true);
        cards.Activate(b); yield return WaitIdle();
        yield return AssertActive(b, true, false);
        PetCharacterCardResult storedA = null;
        yield return Get<PetCharacterCardResult>("/api/characters/" + a.id, value => storedA = value);
        Require(storedA?.card?.actions?.FirstOrDefault(x => x.id == "wave")?.enabled == true,
            "Switching inside the debounce interval lost A's action change.");
        a = storedA.card;
        cards.Activate(a); yield return WaitIdle();
        yield return AssertActive(a, true, true);
        report.AppendLine("PASS: rapid A→B→A switching preserves pending actions and synchronizes name/persona/model/voice.");

        var updated = Clone(a); updated.name += "-已编辑"; updated.persona = "我是测试角色 A，喜欢安静地陪伴。";
        updated.actions = PetActionLibrary.Export();
        cards.Save(updated, false); yield return WaitIdle();
        cards.Refresh(); yield return WaitIdle();
        a = cards.Cards.First(x => x.id == a.id);
        Require(a.name == updated.name && a.persona == updated.persona, "Saved identity disappeared after refreshing the card list.");
        yield return AssertActive(a, true, true);
        report.AppendLine("PASS: saving the active card updates backend and frontend identity, and survives refresh.");

        string priorModel = models.SelectedModelId, priorName = chat.CharacterName;
        var invalid = Clone(a); invalid.name = "不得提交的模型失败修改"; invalid.model_id = "model-missing-qa";
        invalid.skins.First(skin => skin.id == invalid.active_skin_id).model_id = invalid.model_id;
        cards.Save(invalid, false); yield return WaitIdle();
        yield return Get<RuntimeSettingsData>("/api/settings", value => settings = value);
        Require(settings.active_character_id == a.id && settings.pet_name == priorName && settings.model_id == priorModel,
            "A failed active-card model change partially committed the backend card.");
        Require(cards.ActiveId == a.id && chat.CharacterName == priorName && models.SelectedModelId == priorModel,
            "A failed model change altered the displayed character.");
        report.AppendLine("PASS: missing-model failure leaves both backend configuration and visible character unchanged.");

        cards.Activate(b); yield return WaitIdle();
        yield return AssertActive(b, true, false);
        cards.Refresh(); yield return WaitIdle();
        Require(cards.Cards.Any(x => x.id == a.id) && cards.Cards.Any(x => x.id == b.id), "Refresh lost a created card.");
        report.AppendLine("PASS: both saved cards remain available and independently selectable after refresh.");

        var outfits = Clone(b);
        outfits.skins = new[] {
            new PetCharacterSkin { id = "daily", name = "日常服饰", model_id = secondModel.id },
            new PetCharacterSkin { id = "alternate", name = "另一套服饰", model_id = PetModelLibrary.BuiltinModelId },
            new PetCharacterSkin { id = "missing", name = "缺失模型", model_id = "model-missing-qa" }
        };
        outfits.active_skin_id = "daily";
        cards.Save(outfits, false); yield return WaitIdle();
        string session = chat.SessionId;
        typeof(PetBackendHttpClient).GetProperty("SpeechEnabled").SetValue(chat, false);
        chat.SendChat("这是换装前的离线测试对话。", false);
        double deadline = EditorApplication.timeSinceStartup + 20;
        while (chat.IsBusy) { Require(EditorApplication.timeSinceStartup < deadline, "Offline chat timeout."); yield return null; }
        var history = (List<PetHistoryMessage>)typeof(PetBackendHttpClient).GetField("history", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(chat);
        int historyCount = history.Count;
        Require(historyCount >= 2, "No completed history available to verify preservation.");
        PetActionLibrary.SetEnabled("nod", true);
        cards.ActivateSkin("alternate"); yield return WaitIdle();
        yield return Get<RuntimeSettingsData>("/api/settings", value => settings = value);
        Require(settings.active_skin_id == "alternate" && settings.model_id == PetModelLibrary.BuiltinModelId && models.SelectedModelId == settings.model_id,
            "Manual outfit switch did not synchronize the visible model and saved skin.");
        Require(chat.SessionId == session && history.Count == historyCount && chat.CharacterName == b.name && settings.pet_persona == b.persona,
            "Switching outfit changed the conversation or character identity.");
        Require(PetActionLibrary.IsEnabled("nod") && settings.actions.First(x => x.id == "nod").enabled, "Pending action preferences were lost on outfit switch.");
        yield return cards.SwitchSkinRoutine("daily", true, () => true); yield return WaitIdle();
        Require(models.SelectedModelId == secondModel.id && chat.SessionId == session && history.Count == historyCount, "AI outfit switching lost the session or model binding.");
        var retained = GameObject.Find("PetCharacterRoot").GetComponentsInChildren<PetCharacterAnimation>().First();
        cards.ActivateSkin("missing"); yield return WaitIdle();
        Require(models.SelectedModelId == secondModel.id && retained != null && retained.gameObject.activeInHierarchy, "Failed outfit load did not retain the existing visual instance.");
        yield return cards.SwitchSkinRoutine("alternate", true, () => false); yield return WaitIdle();
        Require(models.SelectedModelId == secondModel.id, "Cancelled reply still applied its outfit.");
        outfits = Clone(cards.Cards.First(x => x.id == b.id));
        outfits.allow_ai_skin_switch = false;
        outfits.skins = outfits.skins.Where(skin => skin.id != "missing").ToArray();
        outfits.skins.First(skin => skin.id == "alternate").name = "已改名服饰";
        cards.Save(outfits, false); yield return WaitIdle();
        yield return cards.SwitchSkinRoutine("alternate", true); yield return WaitIdle();
        Require(models.SelectedModelId == secondModel.id, "Disabled AI outfit switch still changed the model.");
        cards.ActivateSkin("alternate"); yield return WaitIdle();
        cards.Refresh(); yield return WaitIdle();
        var savedOutfits = cards.Cards.First(x => x.id == b.id);
        Require(savedOutfits.skins.Length == 2 && savedOutfits.active_skin_id == "alternate" && savedOutfits.skins.Any(x => x.name == "已改名服饰"), "Skin edits disappeared after refresh.");
        report.AppendLine("PASS: outfit add/rename/remove/save, manual and AI switching preserve session/history/identity/pending actions; missing models/cancelled replies/disabled AI keep the previous outfit.");
    }

    private static IEnumerator Create(PetCharacterCard draft, Action<PetCharacterCard> receive)
    {
        RequireSafeUrl(); cards.Save(draft, true); yield return WaitIdle();
        var saved = cards.Cards.FirstOrDefault(x => x.name == draft.name);
        Require(saved != null, "Could not create card: " + cards.Status);
        createdIds.Add(saved.id); Remember(ActionKey(saved.id)); receive(Clone(saved));
    }

    private static IEnumerator AssertActive(PetCharacterCard expected, bool wave, bool nod)
    {
        RuntimeSettingsData actual = null;
        yield return Get<RuntimeSettingsData>("/api/settings", value => actual = value);
        Require(actual != null && actual.active_character_id == expected.id, "Backend active character differs from selected card.");
        Require(cards.ActiveId == expected.id && chat.CharacterId == expected.id && chat.CharacterName == expected.name,
            "Frontend identity did not follow the selected card.");
        Require(actual.pet_name == expected.name && actual.pet_persona == expected.persona, "Backend identity was not synchronized.");
        Require(actual.default_tts_voice == expected.default_tts_voice, "Voice configuration was not synchronized.");
        Require(models.SelectedModelId == expected.model_id && actual.model_id == expected.model_id, "Visual model and backend binding differ.");
        Require(PetActionLibrary.IsEnabled("wave") == wave && PetActionLibrary.IsEnabled("nod") == nod, "Action enable states leaked between cards.");
        Require(PetActionLibrary.Entries.Any(x => !x.builtIn && x.id == expected.actions.First(x => !x.builtIn).id),
            "The selected card's custom action is absent.");
    }

    private static PetCharacterCard NewCard(string name, string model, bool wave, bool nod, string voice)
    {
        var actions = PetActionLibrary.BuiltInIds.Select((id, index) => new PetActionEntry {
            id = id, label = PetActionLibrary.BuiltInLabels[index], builtIn = true,
            enabled = id == "wave" ? wave : id == "nod" ? nod : true
        }).ToList();
        actions.Add(new PetActionEntry { id = "custom_" + Guid.NewGuid().ToString("N"), label = name + "组合",
            steps = new[] { new PetActionStep { action = "happy", seconds = 1 }, new PetActionStep { action = "think", seconds = 1 } } });
        return new PetCharacterCard { name = name, persona = "我是" + name + "，以第一人称描述自己。", model_id = model,
            default_tts_voice = voice, actions = actions.ToArray() };
    }

    private static IEnumerator WaitIdle()
    {
        double deadline = EditorApplication.timeSinceStartup + 75;
        do
        {
            RequireSafeUrl();
            Require(EditorApplication.timeSinceStartup < deadline, "Card operation timed out: " + cards.Status);
            yield return null;
        } while (cards.IsBusy || models.IsBusy);
    }

    private static IEnumerator Get<T>(string path, Action<T> receive)
    {
        using var request = UnityWebRequest.Get(TestUrl + path);
        request.timeout = 15; yield return request.SendWebRequest();
        Require(request.result == UnityWebRequest.Result.Success, "Isolated backend request failed: " + path + " (" + request.responseCode + ")");
        receive(JsonUtility.FromJson<T>(request.downloadHandler.text));
    }

    private static IEnumerator Restore()
    {
        if (cards == null || models == null || api == null) yield break;
        api.SetBackendBaseUrl(TestUrl);
        if (cards.IsBusy || models.IsBusy) yield return WaitIdle();
        cards.Activate(originalCard); yield return WaitIdle();
        Require(cards.ActiveId == originalId, "Could not restore the original isolated-backend character.");
        foreach (string id in createdIds)
        {
            using var request = PetBackendHttpClient.JsonRequest(TestUrl + "/api/characters/" + id, "DELETE", "{}");
            request.timeout = 15; yield return request.SendWebRequest();
            Require(request.result == UnityWebRequest.Result.Success, "Could not remove temporary test card " + id);
        }
        cards.Refresh(); yield return WaitIdle();
        if (models.SelectedModelId != originalModel) { models.SelectModel(originalModel); yield return WaitIdle(); }
        report.AppendLine("PASS: original active card/model restored and temporary cards removed from the disposable backend.");
    }

    private static void Remember(string key) { if (!savedPrefs.ContainsKey(key)) savedPrefs[key] = PlayerPrefs.HasKey(key) ? PlayerPrefs.GetString(key) : null; }
    private static string ActionKey(string id) => string.IsNullOrEmpty(id) || id == "default" ? PetActionLibrary.PrefsKey : PetActionLibrary.PrefsKey + "." + id;
    private static PetCharacterCard Clone(PetCharacterCard value) => value == null ? null : JsonUtility.FromJson<PetCharacterCard>(JsonUtility.ToJson(value));
    private static void RestoreLocal()
    {
        if (chat != null) typeof(PetBackendHttpClient).GetProperty("SpeechEnabled").SetValue(chat, originalSpeech);
        if (originalActions != null) PetActionLibrary.ApplyCharacter(originalScope, originalActions);
        if (api != null && !string.IsNullOrEmpty(originalUrl)) api.SetBackendBaseUrl(originalUrl);
        foreach (var pair in savedPrefs)
            if (pair.Value == null) PlayerPrefs.DeleteKey(pair.Key); else PlayerPrefs.SetString(pair.Key, pair.Value);
        PlayerPrefs.Save();
        PetActionLibrary.Reload();
    }
    private static void RequireSafeUrl()
    {
        Require(DesktopPetPrefs.GetBackendBaseUrl() == TestUrl,
            "Configure the disposable offline backend at http://127.0.0.1:18081 before running this check. No production backend is permitted.");
    }
    private static void Require(bool valid, string message) { if (!valid) throw new InvalidOperationException(message); }
    private static void OnPlayMode(PlayModeStateChange state)
    {
        if (!running || state != PlayModeStateChange.ExitingPlayMode) return;
        failure = "Play mode ended before asynchronous checks and backend cleanup completed.";
        RestoreLocal(); Finish();
    }
    private static void Finish()
    {
        EditorApplication.playModeStateChanged -= OnPlayMode;
        running = false;
        string text = (failure == null ? "PASSED\n" : "FAILED: " + failure + "\n") + report
            + "Only the explicitly configured disposable localhost:18081 backend was used. Local preference keys were restored.\n";
        File.WriteAllText(Path.Combine(directory, "character-card-checks.txt"), text, new UTF8Encoding(false));
        if (failure == null) Debug.Log("CHARACTER_CARD_CHECKS_PASSED\n" + text); else Debug.LogError("CHARACTER_CARD_CHECKS_FAILED\n" + text);
    }
}

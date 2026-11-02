using System;
using System.Collections;
using System.Linq;
using UnityEngine;
using UnityEngine.Networking;

[Serializable]
public sealed class PetCharacterSkin
{
    public string id, name, model_id;
}
[Serializable]
public sealed class PetCharacterCard
{
    public string id, name, persona, model_id;
    public PetCharacterSkin[] skins = Array.Empty<PetCharacterSkin>();
    public string active_skin_id = "default";
    public bool allow_ai_skin_switch = true;
    public PetActionEntry[] actions = Array.Empty<PetActionEntry>();
    public string default_tts_voice = "zh-CN-XiaoxiaoNeural";
    public string tts_provider = "edge", tts_base_url = "", tts_api_key = "", tts_model = "gpt-4o-mini-tts", tts_voice = "alloy";
    public string asr_base_url = "", asr_api_key = "", asr_model = "whisper-1";
    public string asr_provider = "openai", windows_asr_language = "zh-CN";
    public string gpt_sovits_ref_audio_path = "", gpt_sovits_prompt_text = "", gpt_sovits_prompt_lang = "zh", gpt_sovits_text_lang = "zh";
    public bool tts_api_key_configured, asr_api_key_configured, clear_tts_api_key, clear_asr_api_key;
}
[Serializable] public sealed class PetCharacterCardList { public string active_character_id; public PetCharacterCard[] cards; }
[Serializable] public sealed class PetCharacterCardResult { public PetCharacterCard card; public RuntimeSettingsData settings; }

/// <summary>Coordinates character identity, model and local actions as one switch.</summary>
[DisallowMultipleComponent]
public sealed class PetCharacterCards : MonoBehaviour
{
    public PetCharacterCard[] Cards { get; private set; } = Array.Empty<PetCharacterCard>();
    public string ActiveId { get; private set; } = "default";
    public bool IsBusy { get; private set; }
    public bool Ready { get; private set; }
    public string Status { get; private set; } = "正在连接角色卡…";
    public event Action Changed;
    public event Action<PetCharacterCard> Saved;
    private BackendSettingsApi api;
    private PetModelLibrary models;
    private PetBackendHttpClient chat;
    private bool applying;
    private float saveActionsAt = -1;
    private int actionRevision;
    private bool savingActions;
    private bool persistenceFailed, lastSwitchSucceeded;
    private float nextModelRetry;
    [Serializable] private sealed class ActionUpdate { public PetActionEntry[] actions; }
    [Serializable] private sealed class ModelUpdate { public string model_id; }
    private string syncedModel;

    public static void EnsureSkins(PetCharacterCard card)
    {
        if (card == null) return;
        if (card.skins == null || card.skins.Length == 0)
            card.skins = new[] { new PetCharacterSkin { id = "default", name = "默认服饰",
                model_id = string.IsNullOrEmpty(card.model_id) ? PetModelLibrary.BuiltinModelId : card.model_id } };
        var active = card.skins.FirstOrDefault(skin => skin != null && skin.id == card.active_skin_id) ?? card.skins.FirstOrDefault(skin => skin != null);
        if (active == null) return;
        card.active_skin_id = active.id;
        card.model_id = active.model_id;
    }

    public void ActivateSkin(string skinId)
    {
        if (!IsBusy) StartCoroutine(SwitchSkinRoutine(skinId, false));
    }

    /// <summary>Load first, then commit the outfit without changing identity or conversation.</summary>
    public IEnumerator SwitchSkinRoutine(string skinId, bool fromReply, Func<bool> stillCurrent = null)
    {
        if (!Ready || IsBusy || string.IsNullOrEmpty(skinId)) yield break;
        var current = Cards.FirstOrDefault(card => card.id == ActiveId);
        if (current == null || (fromReply && !current.allow_ai_skin_switch)) yield break;
        if (!fromReply && chat != null && (chat.IsBusy || chat.IsSpeaking))
        { SetStatus("请等待当前回复和朗读结束后再穿戴服饰。"); yield break; }
        var interaction = GetComponent<DesktopPetInteraction>();
        if (interaction != null && (interaction.IsRecording || interaction.IsTranscribing))
        { SetStatus("请先结束语音输入，再切换服饰。"); yield break; }
        string expected = ActiveId;
        IsBusy = true; applying = true;
        bool wasSwitching = chat != null && chat.CharacterSwitching;
        if (chat != null) chat.CharacterSwitching = true;
        bool transaction = false;
        try
        {
            while (savingActions || models.IsBusy) yield return null;
            if (saveActionsAt >= 0)
            {
                yield return PersistActions();
                if (persistenceFailed) yield break;
            }
            PetCharacterCardResult fresh = null; string error = null;
            yield return Request<PetCharacterCardResult>("/api/characters/" + Uri.EscapeDataString(expected), "GET", null,
                value => fresh = value, value => error = value);
            if (fresh?.card == null) { SetStatus(error ?? "无法读取服饰列表。"); yield break; }
            current = fresh.card; EnsureSkins(current); Upsert(current);
            foreach (var outfit in current.skins)
                if (string.IsNullOrEmpty(outfit.model_id)) outfit.model_id = PetModelLibrary.BuiltinModelId;
            var skin = current.skins.FirstOrDefault(item => item != null && item.id == skinId);
            if (skin == null || (fromReply && !current.allow_ai_skin_switch))
            { SetStatus("未更换服饰：该皮肤未保存到当前角色，或自动换装已关闭。"); yield break; }
            if (stillCurrent != null && !stillCurrent()) yield break;
            if (skin.id == current.active_skin_id && models.SelectedModelId == skin.model_id) yield break;
            api.InvalidateRequests();
            SetStatus("正在穿戴“" + skin.name + "”…");
            models.BeginSelectionTransaction(); transaction = true;
            models.SelectModel(skin.model_id);
            while (models.IsBusy) yield return null;
            if (models.StatusIsError || models.SelectedModelId != skin.model_id)
            { SetStatus("服饰未更换：" + models.Status); yield break; }
            if (ActiveId != expected || (stillCurrent != null && !stillCurrent())) yield break;
            PetCharacterCardResult result = null;
            yield return Request<PetCharacterCardResult>("/api/characters/" + Uri.EscapeDataString(expected) + "/skins/" + Uri.EscapeDataString(skin.id) + "/activate",
                "POST", "{}", value => result = value, value => error = value);
            if (result?.settings == null)
            {
                // A lost response may follow a successful commit. Reconcile before rolling back.
                RuntimeSettingsData actual = null;
                yield return Request<RuntimeSettingsData>("/api/settings", "GET", null, value => actual = value, _ => { });
                if (actual != null && actual.active_character_id == expected && actual.active_skin_id == skin.id && actual.model_id == skin.model_id)
                { current.active_skin_id = skin.id; current.model_id = skin.model_id; result = new PetCharacterCardResult { card = current, settings = actual }; }
            }
            if (result?.settings == null || result.settings.active_character_id != expected || result.settings.model_id != skin.model_id)
            { SetStatus(error ?? "服饰保存失败，已保留原外观。"); yield break; }
            models.CompleteSelectionTransaction(true); transaction = false;
            syncedModel = skin.model_id;
            Upsert(result.card ?? current);
            api.PublishSettings(result.settings);
            SetStatus("已穿戴“" + skin.name + "”，聊天与角色设定已保留。");
        }
        finally
        {
            if (transaction) models.CompleteSelectionTransaction(false);
            applying = false; IsBusy = false;
            if (chat != null) chat.CharacterSwitching = wasSwitching;
            Changed?.Invoke();
        }
    }

    private IEnumerator Start()
    {
        api = GetComponent<BackendSettingsApi>(); models = GetComponent<PetModelLibrary>(); chat = GetComponent<PetBackendHttpClient>();
        api.SettingsSaved += SettingsChanged;
        PetActionLibrary.Changed += ActionsChanged;
        while (models != null && models.IsBusy) yield return null;
        yield return Load(true);
    }
    private void OnDestroy() { if (api != null) api.SettingsSaved -= SettingsChanged; PetActionLibrary.Changed -= ActionsChanged; }
    private void Update()
    {
        if (!Ready || IsBusy || applying || savingActions) return;
        if (saveActionsAt >= 0 && Time.unscaledTime >= saveActionsAt) StartCoroutine(PersistActions());
        if (models != null && !models.IsBusy && !models.StatusIsError && models.SelectedModelId != syncedModel && Time.unscaledTime >= nextModelRetry)
            StartCoroutine(PersistModel());
    }
    private void SetStatus(string text) { Status = text; Changed?.Invoke(); }
    public void Refresh() { if (!IsBusy) StartCoroutine(Load(false)); }
    private IEnumerator Load(bool startup)
    {
        IsBusy = true; SetStatus("正在读取角色卡…");
        PetCharacterCardList list = null; string error = null;
        yield return Request<PetCharacterCardList>("/api/characters", "GET", null, value => list = value, value => error = value);
        if (list?.cards == null) { IsBusy = false; SetStatus(error ?? "角色卡响应无效。"); yield break; }
        Cards = list.cards; ActiveId = list.active_character_id;
        foreach (var item in Cards) EnsureSkins(item);
        if (startup)
        {
            var active = Cards.FirstOrDefault(card => card.id == ActiveId);
            if (active != null)
            {
                // Adopt pre-card local selections once, so an upgrade preserves the user's setup.
                if (ActiveId == "default" && string.IsNullOrEmpty(active.model_id) && (active.actions == null || active.actions.Length == 0))
                {
                    active.model_id = models != null ? models.SelectedModelId : PetModelLibrary.BuiltinModelId;
                    active.skins.First(skin => skin.id == active.active_skin_id).model_id = active.model_id;
                    active.actions = PetActionLibrary.Export();
                    yield return Request<PetCharacterCardResult>("/api/characters/default", "PUT", JsonUtility.ToJson(active), _ => { }, value => error = value);
                }
                IsBusy = false;
                yield return ActivateRoutine(active, false);
                yield break;
            }
        }
        IsBusy = false; Ready = true; SetStatus("共 " + Cards.Length + " 张角色卡。选择卡片可编辑或切换。");
    }
    public void Save(PetCharacterCard card, bool create)
    {
        if (!IsBusy) StartCoroutine(SaveRoutine(card, create));
    }
    private IEnumerator SaveRoutine(PetCharacterCard card, bool create)
    {
        EnsureSkins(card);
        if (!create && card.id == ActiveId)
        {
            yield return ActivateRoutine(card, false, true);
            if (lastSwitchSucceeded) { yield return Load(false); Saved?.Invoke(Cards.First(item => item.id == card.id)); SetStatus("当前角色卡已保存并同步。"); }
            yield break;
        }
        if (create) card.id = "character_" + Guid.NewGuid().ToString("N");
        IsBusy = true; SetStatus("正在保存角色卡…");
        PetCharacterCardResult result = null; string error = null;
        string path = "/api/characters" + (create ? "" : "/" + Uri.EscapeDataString(card.id));
        yield return Request<PetCharacterCardResult>(path, create ? "POST" : "PUT", JsonUtility.ToJson(card), value => result = value, value => error = value);
        IsBusy = false;
        if (result?.card == null) { SetStatus(error ?? "保存失败。"); yield break; }
        yield return Load(false);
        Saved?.Invoke(result.card);
        SetStatus("角色卡已保存。" + (create ? "点击对应卡片的“切换”即可使用。" : ""));
    }
    public void Delete(string id)
    {
        if (!IsBusy && id != "default" && id != ActiveId) StartCoroutine(DeleteRoutine(id));
    }
    private IEnumerator DeleteRoutine(string id)
    {
        IsBusy = true; string error = null;
        yield return Request<PetApiError>("/api/characters/" + Uri.EscapeDataString(id), "DELETE", "{}", _ => { }, value => error = value);
        IsBusy = false;
        if (error != null) { SetStatus(error); yield break; }
        yield return Load(false); SetStatus("角色卡已删除。");
    }
    public void Activate(PetCharacterCard card) { if (!IsBusy && card != null) StartCoroutine(ActivateRoutine(card, true)); }
    private IEnumerator ActivateRoutine(PetCharacterCard card, bool resetConversation, bool saveCard = false)
    {
        EnsureSkins(card);
        lastSwitchSucceeded = false;
        var interaction = GetComponent<DesktopPetInteraction>();
        if (interaction != null && (interaction.IsRecording || interaction.IsTranscribing))
        { SetStatus("请先结束语音输入，再切换角色。"); yield break; }
        bool transaction = false;
        bool wasSwitching = chat != null && chat.CharacterSwitching;
        try
        {
        IsBusy = true; applying = true;
        while (savingActions) yield return null;
        if (Ready && saveActionsAt >= 0)
        {
            yield return PersistActions();
            if (persistenceFailed) { applying = false; IsBusy = false; yield break; }
        }
        if (!saveCard)
        {
            PetCharacterCardList latest = null; string readError = null;
            yield return Request<PetCharacterCardList>("/api/characters", "GET", null, value => latest = value, value => readError = value);
            var found = latest?.cards?.FirstOrDefault(item => item.id == card.id);
            if (found == null) { applying = false; IsBusy = false; SetStatus(readError ?? "角色卡已不存在，请刷新列表。"); yield break; }
            Cards = latest.cards; card = found;
        }
        api.InvalidateRequests();
        chat.CharacterSwitching = true;
        chat.CancelCurrentRequest();
        GetComponent<DesktopPetProactiveScreen>()?.PauseForCharacterSwitch();
        GetComponent<PetDataPanel>()?.ResetForCharacterSwitch();
        string desired = string.IsNullOrEmpty(card.model_id) ? PetModelLibrary.BuiltinModelId : card.model_id;
        SetStatus("正在切换到 " + card.name + "…");
        while (models.IsBusy) yield return null;
        models.BeginSelectionTransaction(); transaction = true;
        models.SelectModel(desired);
        while (models.IsBusy) yield return null;
        if (models.SelectedModelId != desired || models.StatusIsError)
        {
            applying = false; IsBusy = false; chat.CharacterSwitching = false;
            SetStatus("角色未切换：" + models.Status); yield break;
        }
        PetCharacterCardResult result = null; string error = null;
        if (saveCard)
        {
            yield return Request<PetCharacterCardResult>("/api/characters/" + Uri.EscapeDataString(card.id), "PUT", JsonUtility.ToJson(card), value => result = value, value => error = value);
            if (result?.card == null)
            {
                applying = false; IsBusy = false; chat.CharacterSwitching = false; SetStatus(error ?? "角色卡保存失败。"); yield break;
            }
        }
        yield return Request<PetCharacterCardResult>("/api/characters/" + Uri.EscapeDataString(card.id) + "/activate", "POST", "{}", value => result = value, value => error = value);
        if (result?.settings == null)
        {
            // A timeout may happen after activation. Read the backend before deciding to roll back.
            RuntimeSettingsData actual = null;
            yield return Request<RuntimeSettingsData>("/api/settings", "GET", null, value => actual = value, _ => { });
            if (actual != null && actual.active_character_id == card.id) result = new PetCharacterCardResult { card = card, settings = actual };
        }
        if (result?.settings == null)
        {
            applying = false; IsBusy = false; chat.CharacterSwitching = false;
            SetStatus(error ?? "切换失败，已恢复原模型。"); yield break;
        }
        ActiveId = card.id; Ready = true; syncedModel = desired; saveActionsAt = -1;
        Upsert(result.card ?? card);
        PetActionLibrary.ApplyCharacter(ActiveId, result.settings.actions);
        chat.SetCharacterIdentity(ActiveId, result.settings.pet_name);
        if (resetConversation) chat.NewConversation();
        api.PublishSettings(result.settings);
        GetComponent<PetSpeechBubble>()?.Hide();
        models.CompleteSelectionTransaction(true); transaction = false;
        applying = false; IsBusy = false; chat.CharacterSwitching = false;
        lastSwitchSucceeded = true;
        SetStatus("已切换到 " + result.settings.pet_name + "，名称、人设、语音、模型和动作已同步。");
        }
        finally
        {
            if (transaction) models.CompleteSelectionTransaction(false);
            applying = false; IsBusy = false;
            if (chat != null) chat.CharacterSwitching = wasSwitching;
        }
    }
    private void SettingsChanged(RuntimeSettingsData data)
    {
        if (data == null || applying) return;
        chat.SetCharacterIdentity(data.active_character_id, data.pet_name);
        var card = Cards.FirstOrDefault(item => item.id == ActiveId);
        if (card != null)
        {
            var updated = JsonUtility.FromJson<PetCharacterCard>(JsonUtility.ToJson(data));
            updated.id = card.id; updated.name = data.pet_name; updated.persona = data.pet_persona;
            if (updated.skins == null || updated.skins.Length == 0) { updated.skins = card.skins; updated.active_skin_id = card.active_skin_id; updated.allow_ai_skin_switch = card.allow_ai_skin_switch; }
            Upsert(updated);
        }
        Changed?.Invoke();
    }
    private void ActionsChanged() { if (applying || !Ready) return; actionRevision++; saveActionsAt = Time.unscaledTime + 0.7f; }
    private IEnumerator PersistActions()
    {
        savingActions = true; int revision = actionRevision; string id = ActiveId; string error = null;
        yield return Request<PetCharacterCardResult>("/api/characters/" + Uri.EscapeDataString(id), "PUT",
            JsonUtility.ToJson(new ActionUpdate { actions = PetActionLibrary.Export() }), value => Upsert(value.card), value => error = value);
        savingActions = false;
        persistenceFailed = error != null;
        if (revision == actionRevision) saveActionsAt = error == null ? -1 : Time.unscaledTime + 5;
        if (error != null) SetStatus("动作已保存在本机；同步角色卡失败：" + error);
    }
    private IEnumerator PersistModel()
    {
        savingActions = true; string selected = models.SelectedModelId; string error = null;
        yield return Request<PetCharacterCardResult>("/api/characters/" + Uri.EscapeDataString(ActiveId), "PUT",
            JsonUtility.ToJson(new ModelUpdate { model_id = selected }), value => Upsert(value.card), value => error = value);
        savingActions = false;
        if (error == null) syncedModel = selected; else nextModelRetry = Time.unscaledTime + 5;
        if (error != null) SetStatus("模型已切换，但保存角色卡失败：" + error);
    }
    private void Upsert(PetCharacterCard card)
    {
        if (card == null) return;
        int index = Array.FindIndex(Cards, item => item.id == card.id);
        if (index >= 0) Cards[index] = card;
        else Cards = Cards.Concat(new[] { card }).ToArray();
    }
    private static IEnumerator Request<T>(string path, string method, string json, Action<T> success, Action<string> failure)
    {
        using var request = method == "GET" ? UnityWebRequest.Get(DesktopPetPrefs.GetBackendBaseUrl() + path)
            : PetBackendHttpClient.JsonRequest(DesktopPetPrefs.GetBackendBaseUrl() + path, method, json ?? "{}");
        request.timeout = 20; yield return request.SendWebRequest();
        if (request.result != UnityWebRequest.Result.Success) { failure(PetBackendHttpClient.ReadError(request)); yield break; }
        T value = default;
        try { value = JsonUtility.FromJson<T>(request.downloadHandler.text); }
        catch (Exception) { failure("服务响应格式不正确。"); yield break; }
        success(value);
    }
}

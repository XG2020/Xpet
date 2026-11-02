using System;
using System.Collections;
using UnityEngine;

/// <summary>Report the loaded model's executable actions. Reports expire on the backend and never start an animation.</summary>
public sealed class PetRuntimeActionReporter : MonoBehaviour
{
    [Serializable] private sealed class Report
    {
        public string character_id;
        public string model_id;
        public string[] actions;
        public bool has_sword;
        public bool model_ready;
    }
    private float nextReport;
    private bool sending;
    private string characterId, modelId;
    private void OnEnable() { PetActionLibrary.Changed += Invalidate; }
    private void OnDisable() { PetActionLibrary.Changed -= Invalidate; StopAllCoroutines(); sending = false; }
    private void Invalidate() { nextReport = 0; }
    private void Update()
    {
        var cards = GetComponent<PetCharacterCards>();
        var models = GetComponent<PetModelLibrary>();
        if (cards == null || models == null || !cards.Ready || cards.IsBusy || models.IsBusy || sending) return;
        if (cards.ActiveId != characterId || models.SelectedModelId != modelId) nextReport = 0;
        if (Time.unscaledTime < nextReport) return;
        nextReport = Time.unscaledTime + 25;
        characterId = cards.ActiveId; modelId = models.SelectedModelId;
        StartCoroutine(Send());
    }
    private IEnumerator Send()
    {
        sending = true;
        var bridge = GetComponent<PetBackendHttpClient>()?.ActionBridge;
        var report = new Report { character_id = characterId, model_id = modelId, has_sword = bridge != null && bridge.HasSword,
            model_ready = bridge != null && bridge.ActiveModelRoot != null && bridge.ActiveModelRoot.gameObject.activeInHierarchy,
            actions = bridge != null ? bridge.GetCallableActionIds().ToArray() : Array.Empty<string>() };
        using (var request = PetBackendHttpClient.JsonRequest(DesktopPetPrefs.GetBackendBaseUrl() + "/api/pet-actions/capabilities", "POST", JsonUtility.ToJson(report)))
        {
            request.timeout = 5;
            yield return request.SendWebRequest();
            if (request.responseCode == 409) nextReport = Time.unscaledTime + 0.8f;
            else if (request.result != UnityEngine.Networking.UnityWebRequest.Result.Success) nextReport = Time.unscaledTime + 5;
        }
        sending = false;
    }
}

using System;
using System.Collections;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Networking;

/// <summary>Opt-in, idle-only observations. Only the backend captures a screen.</summary>
public sealed class DesktopPetProactiveScreen : MonoBehaviour
{
    [SerializeField] private PetBackendHttpClient backendClient;
    [SerializeField] private BackendSettingsApi settingsApi;
    [SerializeField] private DesktopPetInteraction interaction;
    [SerializeField] private DesktopPetSystemController systemController;
    [SerializeField] private SettingsPanelController settingsController;
    [SerializeField] private GameObject chatRoot;
    private PetCharacterAnimation character;
    private bool allowed;
    private bool locallyPaused;
    private bool pollingSettings;
    private int intervalSeconds = 90;
    private float nextSettingsPoll;
    private float nextObservation;
    private int generation;
    private UnityWebRequest activeRequest;
    private UnityWebRequest settingsRequest;

    [Serializable] private class ObservationRequest { public string request_id; }
    [Serializable] private class ObservationResponse {
        public string request_id, status, text, action, emotion;
        public int retry_after_seconds;
    }

    private void Start()
    {
        if (backendClient == null) backendClient = GetComponent<PetBackendHttpClient>();
        if (settingsApi == null) settingsApi = FindFirstObjectByType<BackendSettingsApi>(FindObjectsInactive.Include);
        if (interaction == null) interaction = GetComponent<DesktopPetInteraction>();
        if (systemController == null) systemController = GetComponent<DesktopPetSystemController>();
        if (settingsController == null) settingsController = FindFirstObjectByType<SettingsPanelController>(FindObjectsInactive.Include);
        character = FindAnyObjectByType<PetCharacterAnimation>();
        if (settingsApi != null) settingsApi.SettingsSaved += ApplySavedSettings;
        nextSettingsPoll = Time.unscaledTime + 2;
        nextObservation = Time.unscaledTime + 90;
    }

    private void Update()
    {
        if (!pollingSettings && Time.unscaledTime >= nextSettingsPoll)
            StartCoroutine(PollSettings());
        if (!allowed || locallyPaused || !CanObserve()) { CancelObservation(); return; }
        if (activeRequest == null && Time.unscaledTime >= nextObservation)
        {
            nextObservation = Time.unscaledTime + intervalSeconds;
            StartCoroutine(Observe());
        }
    }

    public bool CanObserve()
    {
        var behavior = GetComponent<PetBehaviorCoordinator>();
        if (behavior != null && !behavior.CanRunAutonomous) return false;
        if (backendClient == null || backendClient.IsBusy || backendClient.IsSpeaking) return false;
        if (systemController != null && !systemController.IsVisible) return false;
        if (interaction != null && (interaction.IsRecording || interaction.IsTranscribing)) return false;
        if (settingsController != null && settingsController.IsOpen) return false;
        if (chatRoot != null && chatRoot.activeInHierarchy) return false;
        if (character == null || !character.gameObject.activeInHierarchy || character.IsDragging) return false;
        var selected = EventSystem.current != null ? EventSystem.current.currentSelectedGameObject : null;
        TMP_InputField input = selected != null ? selected.GetComponent<TMP_InputField>() : null;
        return input == null || !input.isFocused;
    }

    public void PauseImmediately()
    {
        locallyPaused = true;
        allowed = false;
        CancelObservation();
        backendClient?.CancelProactiveReply();
    }

    public void PauseForCharacterSwitch()
    {
        CancelObservation();
        backendClient?.CancelProactiveReply();
        nextObservation = Time.unscaledTime + intervalSeconds;
        character = null;
    }

    public void AcceptOptInSave(RuntimeSettingsData data)
    {
        locallyPaused = false;
        ApplySavedSettings(data);
    }

    private void ApplySavedSettings(RuntimeSettingsData data)
    {
        if (data == null) return;
        character = FindAnyObjectByType<PetCharacterAnimation>();
        bool wasAllowed = allowed;
        allowed = data.enable_proactive_screen && !locallyPaused;
        intervalSeconds = Mathf.Clamp(data.proactive_screen_interval_seconds > 0 ? data.proactive_screen_interval_seconds : 90, 30, 900);
        if (!allowed) { CancelObservation(); backendClient?.CancelProactiveReply(); }
        if (allowed && !wasAllowed) nextObservation = Time.unscaledTime + intervalSeconds;
    }

    private IEnumerator PollSettings()
    {
        pollingSettings = true;
        nextSettingsPoll = Time.unscaledTime + 5;
        using var request = UnityWebRequest.Get(DesktopPetPrefs.GetBackendBaseUrl() + "/api/settings");
        settingsRequest = request;
        request.timeout = 5;
        yield return request.SendWebRequest();
        settingsRequest = null;
        pollingSettings = false;
        if (request.result != UnityWebRequest.Result.Success) { SuspendUntilSettingsReturn(); yield break; }
        RuntimeSettingsData data = null;
        try { data = JsonUtility.FromJson<RuntimeSettingsData>(request.downloadHandler.text); } catch (Exception) { }
        if (data == null) { SuspendUntilSettingsReturn(); yield break; }
        ApplySavedSettings(data);
    }

    private void SuspendUntilSettingsReturn()
    {
        allowed = false;
        CancelObservation();
        backendClient?.CancelProactiveReply();
    }

    private IEnumerator Observe()
    {
        int version = generation;
        string requestId = Guid.NewGuid().ToString("N");
        using var request = PetBackendHttpClient.JsonRequest(DesktopPetPrefs.GetBackendBaseUrl() + "/api/proactive/screen", "POST",
            JsonUtility.ToJson(new ObservationRequest { request_id = requestId }));
        request.timeout = 65;
        activeRequest = request;
        var operation = request.SendWebRequest();
        while (!operation.isDone)
        {
            if (version != generation || !allowed || locallyPaused || !CanObserve()) {
                request.Abort(); if (activeRequest == request) activeRequest = null; yield break;
            }
            yield return null;
        }
        if (activeRequest == request) activeRequest = null;
        if (version != generation || !allowed || locallyPaused || !CanObserve()) yield break;
        if (request.result != UnityWebRequest.Result.Success) yield break;
        ObservationResponse response = null;
        try { response = JsonUtility.FromJson<ObservationResponse>(request.downloadHandler.text); } catch (Exception) { }
        if (response == null || response.request_id != requestId) yield break;
        if (response.retry_after_seconds > 0)
            nextObservation = Mathf.Max(nextObservation, Time.unscaledTime + Mathf.Clamp(response.retry_after_seconds, 5, 900));
        if (response.status == "speak" && !string.IsNullOrWhiteSpace(response.text))
            backendClient.PresentProactiveReply(response.text, response.action, response.emotion);
    }

    private void CancelObservation()
    {
        if (activeRequest == null) return;
        ++generation;
        activeRequest.Abort();
        activeRequest = null;
        nextObservation = Time.unscaledTime + intervalSeconds;
    }

    private void OnDisable()
    {
        CancelObservation();
        if (settingsRequest != null) settingsRequest.Abort();
        settingsRequest = null;
        pollingSettings = false;
        backendClient?.CancelProactiveReply();
        StopAllCoroutines();
    }
    private void OnDestroy() { if (settingsApi != null) settingsApi.SettingsSaved -= ApplySavedSettings; }
}

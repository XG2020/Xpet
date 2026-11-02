using System;
using System.Linq;
using TMPro;
using UnityEngine;

/// <summary>The dedicated model import page's model library controls.</summary>
public sealed class PetModelSettingsPanel : MonoBehaviour
{
    [SerializeField] private PetModelLibrary library;
    [SerializeField] private TMP_Dropdown modelDropdown;
    [SerializeField] private UnityEngine.UI.Button refreshButton;
    [SerializeField] private UnityEngine.UI.Button switchButton;
    [SerializeField] private UnityEngine.UI.Button importButton;
    [SerializeField] private TMP_Text statusText;
    private string[] ids = Array.Empty<string>();
    private string shownSelection;
    private bool bound;

    private void OnEnable()
    {
        if (library == null) library = FindFirstObjectByType<PetModelLibrary>(FindObjectsInactive.Include);
        if (library == null) return;
        library.StatusChanged += RefreshView;
        if (refreshButton != null) refreshButton.onClick.AddListener(RefreshModels);
        if (switchButton != null) switchButton.onClick.AddListener(SwitchModel);
        if (importButton != null) importButton.onClick.AddListener(ImportModel);
        bound = true;
        RefreshView();
    }

    private void OnDisable()
    {
        if (!bound) return;
        if (library != null) library.StatusChanged -= RefreshView;
        if (refreshButton != null) refreshButton.onClick.RemoveListener(RefreshModels);
        if (switchButton != null) switchButton.onClick.RemoveListener(SwitchModel);
        if (importButton != null) importButton.onClick.RemoveListener(ImportModel);
        bound = false;
    }

    private void RefreshView()
    {
        if (library == null) return;
        string pending = modelDropdown != null && modelDropdown.value < ids.Length ? ids[modelDropdown.value] : library.SelectedModelId;
        if (shownSelection != library.SelectedModelId) pending = library.SelectedModelId;
        shownSelection = library.SelectedModelId;
        ids = library.ModelEntries.Select(entry => entry.id).ToArray();
        if (modelDropdown != null)
        {
            modelDropdown.ClearOptions();
            modelDropdown.AddOptions(library.ModelEntries.Select(entry => new TMP_Dropdown.OptionData(entry.label)).ToList());
            int selected = Array.IndexOf(ids, pending);
            modelDropdown.SetValueWithoutNotify(Mathf.Max(0, selected));
            modelDropdown.RefreshShownValue();
            modelDropdown.interactable = !library.IsBusy;
        }
        if (refreshButton != null) refreshButton.interactable = !library.IsBusy;
        if (switchButton != null) switchButton.interactable = !library.IsBusy && ids.Length > 0;
        if (importButton != null) importButton.interactable = !library.IsBusy;
        if (statusText != null) { statusText.richText = false; statusText.text = library.Status; }
    }

    private void RefreshModels() { library?.RefreshModels(); }
    private void SwitchModel()
    {
        if (library == null || modelDropdown == null || modelDropdown.value >= ids.Length) return;
        modelDropdown.Hide();
        library.SelectModel(ids[modelDropdown.value]);
    }
    private void ImportModel()
    {
        if (library == null || library.IsBusy) return;
        try
        {
            string path = PetModelFileDialog.PickModel();
            if (!string.IsNullOrWhiteSpace(path)) library.ImportModel(path);
        }
        catch (Exception error) { if (statusText != null) statusText.text = error.Message; }
    }
}

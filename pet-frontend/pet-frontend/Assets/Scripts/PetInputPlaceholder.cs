using TMPro;
using UnityEngine;

/// <summary>Shows a grey empty-value hint only while a TMP input is unfocused.</summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(TMP_InputField))]
[DefaultExecutionOrder(100)]
public sealed class PetInputPlaceholder : MonoBehaviour
{
    public static readonly Color HintColor = new Color32(160, 160, 160, 255);
    private TMP_InputField input;

    public static void Install(TMP_InputField field)
    {
        if (field == null) return;
        var helper = field.GetComponent<PetInputPlaceholder>()
            ?? field.gameObject.AddComponent<PetInputPlaceholder>();
        helper.Refresh();
    }

    private void OnEnable()
    {
        input = GetComponent<TMP_InputField>();
        input.onSelect.AddListener(OnSelect);
        input.onDeselect.AddListener(OnDeselect);
        Refresh();
    }

    private void OnDisable()
    {
        if (input == null) return;
        input.onSelect.RemoveListener(OnSelect);
        input.onDeselect.RemoveListener(OnDeselect);
    }

    private void OnSelect(string value)
    {
        if (input.placeholder != null) input.placeholder.enabled = false;
    }

    private void OnDeselect(string value) => Refresh();

    // TMP updates placeholder.enabled itself when text or focus changes, including
    // SetTextWithoutNotify. Run after that update without modifying the actual text.
    private void LateUpdate() => Refresh();

    private void Refresh()
    {
        if (input == null) input = GetComponent<TMP_InputField>();
        if (input == null || input.placeholder == null) return;
        input.placeholder.color = HintColor;
        input.placeholder.enabled = !input.isFocused && string.IsNullOrEmpty(input.text);
    }
}

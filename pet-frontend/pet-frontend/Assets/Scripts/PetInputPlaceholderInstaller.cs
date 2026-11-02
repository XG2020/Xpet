using System.Collections;
using TMPro;
using UnityEngine;

/// <summary>Installs hints on existing scene inputs once, after initial UI creation.</summary>
public sealed class PetInputPlaceholderInstaller : MonoBehaviour
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void InstallAfterSceneLoad()
    {
        new GameObject("InputPlaceholderInstaller").AddComponent<PetInputPlaceholderInstaller>();
    }

    private IEnumerator Start()
    {
        yield return null;
        yield return null;
        foreach (var input in FindObjectsByType<TMP_InputField>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            PetInputPlaceholder.Install(input);
        Destroy(gameObject);
    }
}

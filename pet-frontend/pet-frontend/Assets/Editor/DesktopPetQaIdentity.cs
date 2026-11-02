using UnityEditor;

/// <summary>Separate PlayerPrefs namespace for integration players; restore before release builds.</summary>
public static class DesktopPetQaIdentity
{
    private const string CompanyKey = "Xpet.QA.originalCompany";
    private const string ProductKey = "Xpet.QA.originalProduct";
    [MenuItem("Desktop Pet/Use Isolated QA Identity")]
    public static void UseQa()
    {
        if (SessionState.GetString(CompanyKey, "") == "") {
            SessionState.SetString(CompanyKey, PlayerSettings.companyName);
            SessionState.SetString(ProductKey, PlayerSettings.productName);
        }
        PlayerSettings.companyName = "DesktopPetQA";
        PlayerSettings.productName = "Xpet Behavior QA";
    }
    [MenuItem("Desktop Pet/Restore Release Identity")]
    public static void Restore()
    {
        PlayerSettings.companyName = SessionState.GetString(CompanyKey, "DesktopPet");
        PlayerSettings.productName = SessionState.GetString(ProductKey, "Xpet");
        SessionState.EraseString(CompanyKey); SessionState.EraseString(ProductKey);
    }
}

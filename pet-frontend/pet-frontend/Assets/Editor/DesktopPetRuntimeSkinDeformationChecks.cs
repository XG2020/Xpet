using UnityEngine;

/// <summary>Editor access to the same BakeMesh diagnostics used by native QA.</summary>
public static class DesktopPetRuntimeSkinDeformationChecks
{
    public static PetRuntimeSkinDeformationDiagnostics.Report Capture(GameObject model)
        => PetRuntimeSkinDeformationDiagnostics.Capture(model);
}

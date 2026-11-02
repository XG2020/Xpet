using System;
using System.IO;
using System.Text.RegularExpressions;
using UnityEngine;

/// <summary>Portable library paths shared by model and imported motion storage.</summary>
public static class PetResourceLibraries
{
    private static string root;
    private static bool migrated;
    private static bool isolated;
    public static string RootDirectory
    {
        get
        {
            if (root != null) return root;
            string[] arguments = Environment.GetCommandLineArgs();
            for (int index = 0; index < arguments.Length; index++)
            {
                if (arguments[index] == "--xpet-isolated-libraries") isolated = true;
                if (arguments[index] == "--xpet-library-root" && index + 1 < arguments.Length)
                    root = Path.GetFullPath(arguments[++index]);
            }
            string qaRoot = Environment.GetEnvironmentVariable("DESKTOP_PET_ISOLATED_DATA_DIR");
            if (!string.IsNullOrWhiteSpace(qaRoot)) { root = Path.GetFullPath(qaRoot); isolated = true; }
            if (root == null)
            {
                var directory = new DirectoryInfo(Application.dataPath);
                while (directory != null)
                {
                    if (Directory.Exists(Path.Combine(directory.FullName, "pet-backend"))
                        || Directory.Exists(Path.Combine(directory.FullName, "pet-models")))
                    { root = directory.FullName; break; }
                    directory = directory.Parent;
                }
                if (root == null) root = Path.GetFullPath(Path.Combine(Application.dataPath, "../../.."));
            }
            return root;
        }
    }

    public static string MotionDirectory
    {
        get
        {
            string destination = Path.Combine(RootDirectory, "pet-actions");
            if (!migrated)
            {
                Directory.CreateDirectory(destination);
                if (!isolated)
                {
                    string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                    foreach (string storage in new[] { Path.Combine(Path.GetDirectoryName(local), "LocalLow", "DesktopPet"), Path.Combine(local, "DesktopPet") })
                        foreach (string product in new[] { "Xpet", "Lita Desktop Pet" })
                            CopyMissingMotions(Path.Combine(storage, product, "pet-motions"), destination);
                    CopyMissingMotions(Path.Combine(Application.persistentDataPath, "pet-motions"), destination);
                }
                else CopyMissingMotions(Path.Combine(RootDirectory, "pet-motions"), destination);
                migrated = true;
            }
            return destination;
        }
    }

    private static void CopyMissingMotions(string source, string destination)
    {
        if (!Directory.Exists(source) || (File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0) return;
        foreach (string path in Directory.GetFiles(source, "*.vmd", SearchOption.TopDirectoryOnly))
        {
            var file = new FileInfo(path);
            if (!Regex.IsMatch(file.Name, "^[a-f0-9]{64}\\.vmd$") || file.Length > PetVmdMotion.MaximumBytes
                || (file.Attributes & FileAttributes.ReparsePoint) != 0) continue;
            string target = Path.Combine(destination, file.Name);
            if (File.Exists(target)) continue;
            try { File.Copy(file.FullName, target, false); }
            catch (IOException) { if (!File.Exists(target)) throw; }
        }
    }
}

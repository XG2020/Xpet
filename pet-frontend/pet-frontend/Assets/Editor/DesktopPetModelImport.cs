using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

/// <summary>Consumes local import requests without starting a second Editor on this project.</summary>
[InitializeOnLoad]
public static class DesktopPetModelImport
{
    [Serializable] private sealed class Request
    {
        public string jobId;
        public string sourcePath;
        public string modelId;
        public string label;
        public string outputDirectory;
    }

    [Serializable] private sealed class Result
    {
        public string jobId;
        public string status;
        public string error;
        public string modelId;
    }

    [Serializable] private sealed class ModelManifest
    {
        public string id;
        public string label;
        public string bundle_file;
        public string unity_version;
        public string source_type;
    }

    [Serializable] private sealed class MaterialMap
    {
        public string source_type;
        public string source;
        public int vertices;
        public MaterialEntry[] materials;
    }

    [Serializable] private sealed class MaterialEntry
    {
        public int index = -1;
        public string name;
        public string texture;
        public float[] color;
        public bool doubleSided;
        public int indexCount;
        public string sphereTexture;
        public int sphereMode;
        public string toonTexture;
    }

    // Only the material data needed by the pet shader is read. Exported shader
    // names, paths and all other JSON properties are treated as inert metadata.
    [Serializable] private sealed class SourceMaterial
    {
        public string m_Name;
        public NamedValue m_Shader;
        public SavedMaterialProperties m_SavedProperties;
    }
    [Serializable] private sealed class NamedValue { public string Name; }
    [Serializable] private sealed class SavedMaterialProperties
    {
        public TextureProperties m_TexEnvs;
        public FloatProperties m_Floats;
        public ColorProperties m_Colors;
    }
    [Serializable] private sealed class TextureProperties { public TextureValue _MainTex; }
    [Serializable] private sealed class TextureValue { public NamedValue m_Texture; }
    [Serializable] private sealed class FloatProperties { public float _CullMode = 2; }
    [Serializable] private sealed class ColorProperties { public ColorValue _Color; public ColorValue _EyeShadowColor; }
    [Serializable] private sealed class ColorValue { public float r = 1, g = 1, b = 1, a = 1; }

    private const long MaximumSourceBytes = 512L * 1024 * 1024;
    private const long MaximumCopiedBytes = 2L * 1024 * 1024 * 1024;
    private static readonly HashSet<string> TextureExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { ".png", ".jpg", ".jpeg", ".tga", ".bmp", ".tif", ".tiff", ".dds" };
    private static readonly Regex Identifier = new Regex("^[a-z0-9][a-z0-9_-]{0,63}$", RegexOptions.CultureInvariant);
    private static readonly Regex JobIdentifier = new Regex("^[A-Za-z0-9][A-Za-z0-9_-]{0,95}$", RegexOptions.CultureInvariant);
    private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);
    private static bool busy;
    private static double nextPoll;
    private static string lastAttemptedRequest;

    private static string ProjectRoot => Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
    private static string QueueRoot => Path.Combine(ProjectRoot, "Temp", "DesktopPetModelImport");
    private static string RequestPath => Path.Combine(QueueRoot, "request.json");
    private static string ModelLibraryRoot => Path.GetFullPath(Path.Combine(ProjectRoot, "..", "..", "pet-models"));

    static DesktopPetModelImport()
    {
        EditorApplication.update += PollQueue;
    }

    private static bool EditorUnavailable => busy || EditorApplication.isCompiling || EditorApplication.isUpdating
        || EditorApplication.isPlayingOrWillChangePlaymode || BuildPipeline.isBuildingPlayer;

    private static void PollQueue()
    {
        if (EditorUnavailable || EditorApplication.timeSinceStartup < nextPoll) return;
        nextPoll = EditorApplication.timeSinceStartup + 1.0;
        if (!File.Exists(RequestPath)) return;
        // The backend writes atomically; this small guard also tolerates a manually
        // written request whose contents are still being flushed.
        if ((DateTime.UtcNow - File.GetLastWriteTimeUtc(RequestPath)).TotalMilliseconds < 300) return;
        string json;
        try { json = ReadSmallJson(RequestPath); }
        catch (IOException) { return; }
        if (json == lastAttemptedRequest) return;
        Request request;
        try { request = JsonUtility.FromJson<Request>(json); }
        catch (ArgumentException) { return; }
        if (request == null || string.IsNullOrWhiteSpace(request.jobId)) return;
        if (HasFinishedResult(request)) { lastAttemptedRequest = json; return; }
        ImportRequest(request, json);
    }

    /// <summary>Also callable through Unity -executeMethod when this project is not already open.</summary>
    [MenuItem("Desktop Pet/Import Queued Model")]
    public static void ImportQueuedModel()
    {
        if (EditorUnavailable) throw new InvalidOperationException("Wait for the Editor to finish compiling, importing or playing.");
        if (!File.Exists(RequestPath)) throw new FileNotFoundException("No desktop pet model import request is queued.", RequestPath);
        string json = ReadSmallJson(RequestPath);
        Request request = JsonUtility.FromJson<Request>(json);
        if (request == null) throw new InvalidDataException("The model import request is empty.");
        if (HasFinishedResult(request))
        {
            Debug.Log("DESKTOP_PET_MODEL_IMPORT_ALREADY_FINISHED: " + request.jobId);
            return;
        }
        ImportRequest(request, json);
    }

    private static bool HasFinishedResult(Request request)
    {
        try
        {
            string output = ValidateOutput(request);
            string path = Path.Combine(output, "result.json");
            if (!File.Exists(path)) return false;
            Result result = JsonUtility.FromJson<Result>(ReadSmallJson(path));
            return result != null && result.jobId == request.jobId && (result.status == "completed" || result.status == "failed");
        }
        catch (Exception) { return false; }
    }

    private static void ImportRequest(Request request, string requestJson)
    {
        busy = true;
        lastAttemptedRequest = requestJson;
        string outputDirectory = null;
        try
        {
            outputDirectory = ValidateOutput(request);
            if (string.IsNullOrEmpty(request.jobId) || !JobIdentifier.IsMatch(request.jobId))
                throw new InvalidDataException("The import job ID is invalid.");
            if (string.IsNullOrWhiteSpace(request.sourcePath) || !Path.IsPathRooted(request.sourcePath))
                throw new InvalidDataException("The model source must be an absolute FBX path.");
            string sourcePath = Path.GetFullPath(request.sourcePath);
            if (!File.Exists(sourcePath) || !string.Equals(Path.GetExtension(sourcePath), ".fbx", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The queued source must be an existing FBX. Convert PMX with the local converter first.");
            if (new FileInfo(sourcePath).Length > MaximumSourceBytes)
                throw new InvalidDataException("The source FBX exceeds the 512 MiB import limit.");
            RejectLink(sourcePath);
            request.label = string.IsNullOrWhiteSpace(request.label) ? request.modelId : request.label.Trim();
            if (request.label.Length > 100) throw new InvalidDataException("The model label is too long.");

            string folder = "Assets/ImportedPets/" + request.modelId;
            string sourceFolder = folder + "/Source";
            string sourceDirectory = Path.GetDirectoryName(sourcePath);
            string destinationDirectory = AbsoluteAssetPath(sourceFolder);
            if (string.Equals(destinationDirectory, sourceDirectory, StringComparison.OrdinalIgnoreCase)
                || IsWithin(destinationDirectory, sourceDirectory) || IsWithin(sourceDirectory, destinationDirectory))
                throw new InvalidDataException("Model staging and original source folders must be separate.");
            Directory.CreateDirectory(destinationDirectory);
            Directory.CreateDirectory(outputDirectory);
            string importedPath = sourceFolder + "/model.fbx";
            File.Copy(sourcePath, AbsoluteAssetPath(importedPath), true);
            CopyAdjacentAssets(sourceDirectory, destinationDirectory);
            string sourceType = PrepareMaterialMap(sourceDirectory, folder, Path.GetFileName(sourcePath));

            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            var importer = AssetImporter.GetAtPath(importedPath) as ModelImporter;
            if (importer == null) throw new InvalidOperationException("Unity did not create an FBX ModelImporter.");
            importer.importCameras = false;
            importer.importLights = false;
            importer.importAnimation = false;
            importer.importBlendShapes = true;
            importer.isReadable = true;
            importer.optimizeGameObjects = false;
            importer.animationType = ModelImporterAnimationType.Generic;
            importer.materialImportMode = ModelImporterMaterialImportMode.ImportStandard;
            importer.SaveAndReimport();
            GameObject imported = AssetDatabase.LoadAssetAtPath<GameObject>(importedPath);
            if (imported == null) throw new InvalidOperationException("Unity could not load the imported model.");

            string prefabPath = PetCompatibleModelBuilder.Build(imported, request.modelId, request.label, folder);
            if (string.IsNullOrWhiteSpace(prefabPath) || !prefabPath.StartsWith(folder + "/", StringComparison.Ordinal)
                || AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath) == null)
                throw new InvalidOperationException("The compatible model builder did not return a prefab in the model folder.");
            AssetDatabase.SaveAssets();
            string bundleName = request.modelId + ".petmodel";
            string buildDirectory = Path.Combine(QueueRoot, "Builds", request.jobId);
            Directory.CreateDirectory(buildDirectory);
            var bundle = new AssetBundleBuild
            {
                assetBundleName = bundleName,
                assetNames = new[] { prefabPath },
                addressableNames = new[] { "model" }
            };
            AssetBundleManifest manifest = BuildPipeline.BuildAssetBundles(buildDirectory, new[] { bundle },
                BuildAssetBundleOptions.ChunkBasedCompression | BuildAssetBundleOptions.StrictMode, BuildTarget.StandaloneWindows64);
            string builtFile = Path.Combine(buildDirectory, bundleName);
            if (manifest == null || !File.Exists(builtFile) || new FileInfo(builtFile).Length == 0)
                throw new InvalidOperationException("Unity failed to build the model AssetBundle.");
            CopyAtomically(builtFile, Path.Combine(outputDirectory, bundleName));
            WriteJsonAtomically(Path.Combine(outputDirectory, "model.json"), new ModelManifest
            {
                id = request.modelId, label = request.label, bundle_file = bundleName,
                unity_version = Application.unityVersion, source_type = sourceType
            });
            WriteJsonAtomically(Path.Combine(outputDirectory, "result.json"), new Result
            {
                jobId = request.jobId, status = "completed", error = "", modelId = request.modelId
            });
            Debug.Log("DESKTOP_PET_MODEL_IMPORT_COMPLETED: " + request.modelId + " -> " + Path.Combine(outputDirectory, bundleName));
        }
        catch (Exception exception)
        {
            if (outputDirectory != null)
            {
                try
                {
                    Directory.CreateDirectory(outputDirectory);
                    WriteJsonAtomically(Path.Combine(outputDirectory, "result.json"), new Result
                    {
                        jobId = request.jobId, status = "failed", error = exception.Message, modelId = request.modelId
                    });
                }
                catch (Exception resultError) { Debug.LogError("Could not save model import failure: " + resultError.Message); }
            }
            Debug.LogError("DESKTOP_PET_MODEL_IMPORT_FAILED: " + exception);
            if (Application.isBatchMode) throw;
        }
        finally { busy = false; }
    }

    private static string ValidateOutput(Request request)
    {
        if (request == null || string.IsNullOrEmpty(request.modelId) || !Identifier.IsMatch(request.modelId))
            throw new InvalidDataException("The model ID must contain only lowercase letters, digits, underscores or hyphens.");
        if (string.IsNullOrWhiteSpace(request.outputDirectory) || !Path.IsPathRooted(request.outputDirectory))
            throw new InvalidDataException("The model output directory must be absolute.");
        string output = Path.GetFullPath(request.outputDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string expected = Path.GetFullPath(Path.Combine(ModelLibraryRoot, request.modelId));
        if (!string.Equals(output, expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The model output must be pet-models/<modelId> within this workspace.");
        if (Directory.Exists(output)) RejectLink(output);
        return output;
    }

    private static void CopyAdjacentAssets(string sourceDirectory, string destinationDirectory)
    {
        long copiedBytes = 0;
        int files = 0;
        foreach (string source in EnumerateCompanions(sourceDirectory, sourceDirectory, 0))
        {
            if (++files > 4096) throw new InvalidDataException("Too many companion files beside this FBX.");
            long length = new FileInfo(source).Length;
            copiedBytes += length;
            if (length > MaximumSourceBytes || copiedBytes > MaximumCopiedBytes)
                throw new InvalidDataException("Model companion textures exceed the import size limit.");
            string relative = source.Substring(sourceDirectory.TrimEnd(Path.DirectorySeparatorChar).Length + 1);
            string destination = Path.GetFullPath(Path.Combine(destinationDirectory, relative));
            if (!IsWithin(destination, destinationDirectory)) throw new InvalidDataException("Invalid companion texture path.");
            Directory.CreateDirectory(Path.GetDirectoryName(destination));
            File.Copy(source, destination, true);
        }
    }

    private static IEnumerable<string> EnumerateCompanions(string root, string directory, int depth)
    {
        if (depth > 6) yield break;
        RejectLink(directory);
        foreach (string file in Directory.EnumerateFiles(directory))
        {
            string extension = Path.GetExtension(file);
            bool materialJson = extension.Equals(".json", StringComparison.OrdinalIgnoreCase)
                && (Path.GetFileName(file).Equals("PetMaterialMap.json", StringComparison.OrdinalIgnoreCase)
                    || Path.GetFileName(directory).Equals("Materials", StringComparison.OrdinalIgnoreCase));
            if (!TextureExtensions.Contains(extension) && !materialJson) continue;
            RejectLink(file);
            yield return file;
        }
        foreach (string child in Directory.EnumerateDirectories(directory))
        {
            string name = Path.GetFileName(child);
            // Do not recursively copy unrelated projects next to a user's FBX.
            if (depth == 0 && !name.Equals("Textures", StringComparison.OrdinalIgnoreCase)
                && !name.Equals("Texture", StringComparison.OrdinalIgnoreCase)
                && !name.Equals("tex", StringComparison.OrdinalIgnoreCase)
                && !name.Equals("Materials", StringComparison.OrdinalIgnoreCase)) continue;
            foreach (string file in EnumerateCompanions(root, child, depth + 1)) yield return file;
        }
    }

    private static string PrepareMaterialMap(string sourceDirectory, string folder, string sourceName)
    {
        string suppliedMap = Path.Combine(sourceDirectory, "PetMaterialMap.json");
        MaterialMap map;
        if (File.Exists(suppliedMap))
        {
            map = JsonUtility.FromJson<MaterialMap>(ReadSmallJson(suppliedMap));
            if (map == null || map.materials == null) throw new InvalidDataException("PetMaterialMap.json has no material list.");
            foreach (MaterialEntry material in map.materials)
            {
                if (material == null) throw new InvalidDataException("Empty entry in PetMaterialMap.json.");
                material.texture = RemapTexture(sourceDirectory, material.texture);
                material.sphereTexture = RemapTexture(sourceDirectory, material.sphereTexture);
                material.toonTexture = RemapTexture(sourceDirectory, material.toonTexture);
            }
        }
        else
        {
            var entries = new List<MaterialEntry>();
            string materialsDirectory = Path.Combine(sourceDirectory, "Materials");
            if (Directory.Exists(materialsDirectory))
            {
                foreach (string path in Directory.GetFiles(materialsDirectory, "*.json").OrderBy(value => value, StringComparer.Ordinal))
                {
                    SourceMaterial material = JsonUtility.FromJson<SourceMaterial>(ReadSmallJson(path));
                    if (material == null) continue;
                    var properties = material.m_SavedProperties;
                    string textureName = properties?.m_TexEnvs?._MainTex?.m_Texture?.Name;
                    string texturePath = FindTexture(sourceDirectory, textureName);
                    ColorValue color = properties?.m_Colors?._Color;
                    string name = string.IsNullOrWhiteSpace(material.m_Name) ? Path.GetFileNameWithoutExtension(path) : material.m_Name;
                    if (name.IndexOf("EyeShadow", StringComparison.OrdinalIgnoreCase) >= 0)
                        color = properties?.m_Colors?._EyeShadowColor ?? color;
                    float[] rgba = color == null ? new[] { 1f, 1f, 1f, 1f } : new[] { color.r, color.g, color.b, color.a };
                    if (name.IndexOf("FaceMask", StringComparison.OrdinalIgnoreCase) >= 0) rgba[3] = 0;
                    entries.Add(new MaterialEntry
                    {
                        index = -1, name = name, texture = texturePath, color = rgba,
                        doubleSided = properties?.m_Floats != null && properties.m_Floats._CullMode == 0
                    });
                }
            }
            map = new MaterialMap { source_type = "fbx", source = sourceName, materials = entries.ToArray() };
        }
        string sourceType = string.Equals(map.source_type, "pmx", StringComparison.OrdinalIgnoreCase) ? "pmx" : "fbx";
        map.source_type = sourceType;
        WriteJsonAtomically(AbsoluteAssetPath(folder + "/PetMaterialMap.json"), map);
        return sourceType;
    }

    private static string FindTexture(string sourceDirectory, string textureName)
    {
        if (string.IsNullOrWhiteSpace(textureName)) return "";
        if (textureName != Path.GetFileName(textureName)) throw new InvalidDataException("Material texture names must not contain paths.");
        foreach (string file in EnumerateCompanions(sourceDirectory, sourceDirectory, 0))
            if (TextureExtensions.Contains(Path.GetExtension(file))
                && (Path.GetFileNameWithoutExtension(file).Equals(textureName, StringComparison.OrdinalIgnoreCase)
                    || Path.GetFileName(file).Equals(textureName, StringComparison.OrdinalIgnoreCase)))
                return RemapTexture(sourceDirectory, file.Substring(sourceDirectory.TrimEnd(Path.DirectorySeparatorChar).Length + 1));
        throw new FileNotFoundException("Material texture was not found beside the FBX: " + textureName);
    }

    private static string RemapTexture(string sourceDirectory, string texture)
    {
        if (string.IsNullOrWhiteSpace(texture)) return "";
        if (Path.IsPathRooted(texture)) throw new InvalidDataException("Material texture paths must be relative.");
        string absolute = Path.GetFullPath(Path.Combine(sourceDirectory, texture));
        if (!IsWithin(absolute, sourceDirectory) || !File.Exists(absolute))
            throw new FileNotFoundException("Material texture is missing or outside the model folder: " + texture);
        RejectLink(absolute);
        return "Source/" + absolute.Substring(sourceDirectory.TrimEnd(Path.DirectorySeparatorChar).Length + 1).Replace('\\', '/');
    }

    private static string AbsoluteAssetPath(string path)
    {
        string absolute = Path.GetFullPath(Path.Combine(ProjectRoot, path));
        if (!IsWithin(absolute, Application.dataPath)) throw new InvalidDataException("Asset path leaves this Unity project's Assets folder.");
        return absolute;
    }

    private static bool IsWithin(string path, string directory)
    {
        string prefix = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static void RejectLink(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Model import paths cannot be symbolic links or junctions: " + path);
    }

    private static string ReadSmallJson(string path)
    {
        if (new FileInfo(path).Length > 4 * 1024 * 1024) throw new InvalidDataException("Model metadata exceeds the 4 MiB limit.");
        return File.ReadAllText(path, Utf8);
    }

    private static void WriteJsonAtomically(string path, object value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        string temporary = path + ".pending";
        File.WriteAllText(temporary, JsonUtility.ToJson(value, true), Utf8);
        ReplaceFile(temporary, path);
    }

    private static void CopyAtomically(string source, string destination)
    {
        string temporary = destination + ".pending";
        File.Copy(source, temporary, true);
        ReplaceFile(temporary, destination);
    }

    private static void ReplaceFile(string temporary, string destination)
    {
        if (File.Exists(destination)) File.Replace(temporary, destination, null);
        else File.Move(temporary, destination);
    }
}

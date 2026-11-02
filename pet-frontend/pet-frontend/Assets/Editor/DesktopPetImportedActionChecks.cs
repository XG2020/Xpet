using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;

/// <summary>Import, parser, persistence and live cancellation regression using an isolated action preference key.</summary>
public static class DesktopPetImportedActionChecks
{
    private static bool running, savedApplying, scratchHadPrefs;
    private static string folder, originalCharacter, scratchPrefs;
    private static PetActionEntry[] originalActions;
    private static PetCharacterCards cards;
    private static PetImportedMotionPlayer player;
    private static GameObject replyFixture;
    private static readonly FieldInfo Applying = typeof(PetCharacterCards).GetField("applying", BindingFlags.NonPublic | BindingFlags.Instance);
    private const string ScratchCharacter = "motion_verification";

    [MenuItem("Desktop Pet/Run VMD Parser Checks")]
    public static void ParserChecks()
    {
        byte[] bytes = Fixture();
        var motion = PetVmdMotion.Read(bytes);
        Require(motion.tracks.Count == 3 && motion.Duration == 2 && motion.FrameCount == 9, "VMD header/track/frame parsing failed.");
        PetVmdMotion.Sample(motion.tracks["頭"], 0.5f, out Vector3 position, out Quaternion rotation);
        Require(position.sqrMagnitude < 0.0001f && Mathf.Abs(Quaternion.Angle(Quaternion.identity, rotation) - 12) < 0.05f, "Quaternion/Bezier midpoint sampling failed.");
        Require(PetVmdMotion.CanonicalBone("左ひざ") == "ひざ.L", "MMD bone canonicalization failed.");
        bool rejected = false;
        try { PetVmdMotion.Read(new byte[54]); } catch (InvalidDataException) { rejected = true; }
        Require(rejected, "Corrupted signature accepted.");
        byte[] truncated = new byte[bytes.Length - 1]; Array.Copy(bytes, truncated, truncated.Length);
        rejected = false; try { PetVmdMotion.Read(truncated); } catch (InvalidDataException) { rejected = true; }
        Require(rejected, "Truncated frame data accepted.");
        byte[] invalidFrame = (byte[])bytes.Clone(); Array.Copy(BitConverter.GetBytes(PetVmdMotion.MaximumFrame + 1), 0, invalidFrame, 69, 4);
        rejected = false; try { PetVmdMotion.Read(invalidFrame); } catch (InvalidDataException) { rejected = true; }
        Require(rejected, "Excessive duration accepted.");
        folder = Path.GetFullPath(Path.Combine(Application.dataPath, "../../../verification/imported-actions")); Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "sample-fk.vmd"), bytes);
        File.WriteAllText(Path.Combine(folder, "parser-results.txt"), "PASSED: VMD 0002 header, Japanese names, frame count/duration, bounded data, truncated/invalid input rejection, quaternion and Bezier interpolation. Fixture: sample-fk.vmd.\n");
        Debug.Log("DESKTOP_PET_VMD_PARSER PASSED");
    }

    [MenuItem("Desktop Pet/Run Imported Action Checks")]
    public static void Run()
    {
        if (!EditorApplication.isPlaying || running) throw new InvalidOperationException("Enter Play mode and wait for the active model; only one import check may run.");
        ParserChecks();
        var bridge = UnityEngine.Object.FindFirstObjectByType<PetAnimatorBridge>();
        Transform model = (Transform)typeof(PetAnimatorBridge).GetField("animatorSearchRoot", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(bridge);
        if (model == null) throw new InvalidOperationException("No active model bound to the animator bridge.");
        cards = UnityEngine.Object.FindFirstObjectByType<PetCharacterCards>();
        if (cards != null && cards.IsBusy) throw new InvalidOperationException("Wait for character switching to finish.");
        originalCharacter = PetActionLibrary.CharacterId; originalActions = PetActionLibrary.Export();
        scratchHadPrefs = PlayerPrefs.HasKey(PetActionLibrary.PrefsKey + "." + ScratchCharacter);
        scratchPrefs = PlayerPrefs.GetString(PetActionLibrary.PrefsKey + "." + ScratchCharacter, "");
        if (cards != null) { savedApplying = (bool)Applying.GetValue(cards); Applying.SetValue(cards, true); }
        PetActionLibrary.ApplyCharacter(ScratchCharacter, Array.Empty<PetActionEntry>());
        player = model.GetComponent<PetImportedMotionPlayer>() ?? model.gameObject.AddComponent<PetImportedMotionPlayer>();
        running = true; EditorApplication.playModeStateChanged += OnMode;
        player.StartCoroutine(Safe(Check(bridge, model)));
    }

    private static IEnumerator Check(PetAnimatorBridge bridge, Transform model)
    {
        var prepare = PetImportedMotionPlayer.Prepare(Path.Combine(folder, "sample-fk.vmd"));
        while (!prepare.IsCompleted) yield return null;
        if (prepare.IsFaulted) throw prepare.Exception.GetBaseException();
        Require(PetImportedMotionPlayer.CountCompatibleBones(model, prepare.Result.motion) >= 2, "Fixture does not match active model.");
        var entry = PetActionLibrary.AddImported("导入回归", prepare.Result.fileName, prepare.Result.motion.Duration, 1, "wave");
        PetActionLibrary.Reload();
        Require(PetActionLibrary.Find(entry.id)?.motion_file == entry.motion_file && PetActionLibrary.ResolveCustom("wave")?.id == entry.id, "Imported metadata/trigger did not persist.");
        Transform visual = model.Find("VisualModel") ?? model;
        Transform head = visual.GetComponentsInChildren<SkinnedMeshRenderer>(true).SelectMany(skin => skin.bones)
            .Distinct().First(bone => bone != null && PetVmdMotion.CanonicalBone(bone.name) == "頭");
        bridge.PreviewAction("idle");
        yield return new WaitForSecondsRealtime(.4f);
        yield return new WaitForEndOfFrame();
        Quaternion before = head.localRotation;
        bridge.PreviewAction(entry.id);
        yield return new WaitForSecondsRealtime(1);
        // Imported poses are applied in LateUpdate after restoring the previous
        // frame in Update. Observe the same pose that is submitted for rendering.
        yield return new WaitForEndOfFrame();
        Require(player.IsPlaying && player.BoundBoneCount >= 2, "Imported preview did not bind/play.");
        Require(Quaternion.Angle(before, head.localRotation) > 3, "Imported motion did not visibly change a deforming head bone.");
        PetActionLibrary.SetEnabled(entry.id, false);
        yield return new WaitForSecondsRealtime(0.45f);
        Require(!player.IsPlaying, "Disabling imported action did not stop it.");
        PetActionLibrary.SetEnabled(entry.id, true); bridge.HandleAction("wave");
        yield return new WaitForSecondsRealtime(0.85f);
        Require(player.IsPlaying, "Automatic imported trigger did not play.");
        bridge.HandleAction("think");
        yield return new WaitForSecondsRealtime(0.4f);
        Require(!player.IsPlaying, "New built-in action did not cancel imported playback.");
        PetActionLibrary.SetTrigger(entry.id, "", true); PetActionLibrary.Reload();
        Require(PetActionLibrary.Find(entry.id).ai_decide && bridge.GetCallableActionIds().Contains(entry.id), "AI option did not persist or missing callable VMD capability.");
        replyFixture = new GameObject("Inactive AI reply gate fixture"); replyFixture.SetActive(false);
        var client = replyFixture.AddComponent<PetBackendHttpClient>(); client.SetCharacterIdentity(ScratchCharacter, "回归角色");
        int selectedCallbacks = 0;
        client.onReplyAction.AddListener(value => { if (value == entry.id) selectedCallbacks++; bridge.HandleAction(value); });
        var reply = new PetChatResponse { text = "嗯，在呢。", action = "idle", action_id = entry.id, character_id = ScratchCharacter };
        typeof(PetBackendHttpClient).GetMethod("AcceptReply",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(client,new object[]{reply,"测试",null,100});
        yield return new WaitForSecondsRealtime(0.85f);
        Require(selectedCallbacks == 1 && player.IsPlaying, "Final reply did not dispatch exactly one custom AI action.");
        PetActionLibrary.SetEnabled(entry.id,false);
        reply = new PetChatResponse { text="嗯。",action="idle",action_id=entry.id,character_id=ScratchCharacter };
        typeof(PetBackendHttpClient).GetMethod("AcceptReply",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(client,new object[]{reply,"测试",null,100});
        Require(reply.action_id == "" && selectedCallbacks == 1, "Disabled AI action passed the final client gate.");
        yield return new WaitForSecondsRealtime(0.4f);
        PetActionLibrary.Delete(entry.id);
        Require(PetActionLibrary.Find(entry.id) == null, "Imported deletion did not persist.");
    }

    private static IEnumerator Safe(IEnumerator sequence)
    {
        while (true)
        {
            object next = null; bool moved = false; string error = null;
            try { moved = sequence.MoveNext(); if (moved) next = sequence.Current; } catch (Exception exception) { error = exception.ToString(); }
            if (error != null) { Finish(error); yield break; }
            if (!moved) { Finish(null); yield break; }
            yield return next;
        }
    }

    private static void Finish(string error)
    {
        if (!running) return; running = false; EditorApplication.playModeStateChanged -= OnMode;
        player?.StopImmediately();
        if (replyFixture != null) { UnityEngine.Object.Destroy(replyFixture); replyFixture = null; }
        PetActionLibrary.ApplyCharacter(originalCharacter, originalActions);
        string key = PetActionLibrary.PrefsKey + "." + ScratchCharacter;
        if (scratchHadPrefs) PlayerPrefs.SetString(key, scratchPrefs); else PlayerPrefs.DeleteKey(key); PlayerPrefs.Save();
        if (cards != null) Applying.SetValue(cards, savedApplying);
        string result = error == null ? "PASSED: local source copy, VMD parse, character-scoped metadata and trigger persistence, real active-model binding and visible bone motion, disabling, built-in interruption, AI-decide persistence, callable capability and final-reply custom dispatch exactly once, disabled final gate, deletion; original preferences restored.\n" : "FAILED: " + error;
        File.WriteAllText(Path.Combine(folder, "playback-results.txt"), result);
        if (error == null) Debug.Log("DESKTOP_PET_IMPORTED_ACTIONS " + result); else Debug.LogError(result);
    }
    private static void OnMode(PlayModeStateChange change) { if (change == PlayModeStateChange.ExitingPlayMode) Finish("Play mode exited during verification."); }
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }

    private static byte[] Fixture()
    {
        using (var stream = new MemoryStream()) using (var writer = new BinaryWriter(stream))
        {
            Pad(writer, Encoding.ASCII.GetBytes("Vocaloid Motion Data 0002"), 30);
            Pad(writer, Encoding.ASCII.GetBytes("DesktopPetFixture"), 20);
            writer.Write((uint)9);
            foreach (string name in new[] { "頭", "左腕", "センター" }) foreach (uint frame in new uint[] { 0, 30, 60 })
            {
                Pad(writer, Encoding.GetEncoding(932).GetBytes(name), 15); writer.Write(frame);
                writer.Write(0f); writer.Write(name == "センター" && frame == 30 ? 0.5f : 0f); writer.Write(0f);
                Quaternion q = frame == 30 ? Quaternion.AngleAxis(name == "頭" ? 24 : name == "左腕" ? 12 : 0, Vector3.forward) : Quaternion.identity;
                writer.Write(q.x); writer.Write(q.y); writer.Write(q.z); writer.Write(q.w);
                byte[] interpolation = new byte[64];
                for (int channel = 0; channel < 4; channel++) { interpolation[channel] = interpolation[channel + 4] = 20; interpolation[channel + 8] = interpolation[channel + 12] = 107; }
                writer.Write(interpolation);
            }
            return stream.ToArray();
        }
    }
    private static void Pad(BinaryWriter writer, byte[] bytes, int count) { writer.Write(bytes); writer.Write(new byte[count - bytes.Length]); }
}

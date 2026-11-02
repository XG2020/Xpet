using System.Text;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>Disposable preview objects test additive gaze without changing scenes or preferences.</summary>
public static class DesktopPetMouseGazeChecks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    [Serializable] private sealed class Result
    {
        public string model, head;
        public int eyes;
        public float maximumHeadYaw, maximumHeadPitch, maximumEyeYaw, maximumEyePitch, maximumRestoreError;
        public float nearHeadYaw, nearHeadPitch, middleHeadYaw, middleHeadPitch, farHeadYaw, farHeadPitch;
        public float maximumHeadStep, maximumEyeStep;
        public bool eyesLedHead, occasionalLooked, occasionalRested;
        public string[] failures;
    }
    [Serializable] private sealed class Report { public bool passed; public Result[] models; }

    [MenuItem("Desktop Pet/Run Mouse Gaze Checks")]
    public static void Run()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("Exit Play Mode before mouse gaze checks.");
        var paths = new List<string> { PetModelSetup.PrefabPath };
        paths.AddRange(AssetDatabase.FindAssets("t:Prefab", new[] { "Assets/ImportedPets" })
            .Select(AssetDatabase.GUIDToAssetPath).Where(path => path.EndsWith("/DesktopPet.prefab", StringComparison.Ordinal)));
        var results = paths.Select(Check).ToArray();
        bool passed = results.All(result => result.failures.Length == 0);
        string directory = Path.GetFullPath(Path.Combine(Application.dataPath, "../../../verification/mouse-gaze"));
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "results.json"), JsonUtility.ToJson(new Report { passed = passed, models = results }, true));
        if (!passed) throw new InvalidOperationException("MOUSE_GAZE_FAILED: " + string.Join("; ", results.SelectMany(result => result.failures.Select(error => result.model + ": " + error))));
        Debug.Log("MOUSE_GAZE_PASSED: " + results.Length + " models; amplitude response, eye-first motion, angle/speed bounds, animation preservation, suppression and occasional timing.");
    }

    private static Result Check(string path)
    {
        var result = new Result { model = path };
        var failures = new List<string>();
        var scene = EditorSceneManager.NewPreviewScene();
        try
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (prefab == null) throw new InvalidOperationException("Missing model prefab.");
            var model = UnityEngine.Object.Instantiate(prefab);
            model.SetActive(false); model.hideFlags = HideFlags.HideAndDontSave;
            UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(model, scene);
            var cameraObject = new GameObject("MouseGazePreviewCamera", typeof(Camera));
            UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(cameraObject, scene);
            var camera = cameraObject.GetComponent<Camera>();
            camera.enabled = false; camera.pixelRect = new Rect(0,0,1200,800);
            camera.transform.position = new Vector3(0, 1.2f, -3.2f); camera.transform.LookAt(new Vector3(0,1.2f,0));
            var animator = model.GetComponent<Animator>();
            var idle = animator.runtimeAnimatorController.animationClips.First(clip => clip.name == "Idle");
            idle.SampleAnimation(model, 0);
            var gaze = PetMouseGaze.InstallOn(model); gaze.BindRig(animator, camera);
            result.head = gaze.HeadBone != null ? gaze.HeadBone.name : ""; result.eyes = gaze.EyeBoneCount;
            if (gaze.HeadBone == null) throw new InvalidOperationException("Head bone not recognized.");
            if (result.eyes != 2) failures.Add("Expected two recognized eye joints.");
            var joints = model.GetComponentsInChildren<Transform>(true);
            var originals = joints.ToDictionary(joint => joint, joint => joint.localRotation);
            var positions = joints.ToDictionary(joint => joint, joint => joint.localPosition);
            var scales = joints.ToDictionary(joint => joint, joint => joint.localScale);
            var skin = model.GetComponentsInChildren<SkinnedMeshRenderer>(true).FirstOrDefault(renderer => renderer.sharedMesh != null && renderer.sharedMesh.blendShapeCount > 0);
            if (skin != null) skin.SetBlendShapeWeight(0, 27.5f);
            var simulate = typeof(PetMouseGaze).GetMethod("Simulate", Private);
            var remove = typeof(PetMouseGaze).GetMethod("RemoveOffsets", Private);
            Vector2 origin = camera.WorldToScreenPoint(gaze.HeadBone.position);
            void Tick(Vector2 pointer, float delta, float now, bool allowed, PetMouseGazeMode mode) => simulate.Invoke(gaze, new object[] { pointer, delta, now, true, allowed, mode });
            void TrackBounds()
            {
                result.maximumHeadYaw = Mathf.Max(result.maximumHeadYaw, Mathf.Abs(gaze.HeadAngles.x));
                result.maximumHeadPitch = Mathf.Max(result.maximumHeadPitch, Mathf.Abs(gaze.HeadAngles.y));
                result.maximumEyeYaw = Mathf.Max(result.maximumEyeYaw, Mathf.Abs(gaze.EyeAngles.x));
                result.maximumEyePitch = Mathf.Max(result.maximumEyePitch, Mathf.Abs(gaze.EyeAngles.y));
            }
            for (int frame = 0; frame < 900; frame++)
            {
                Vector2 offset = new Vector2(frame < 450 ? 1400 : -1400, frame < 300 ? 900 : -900);
                Tick(origin + offset, 1f/30, frame/30f, true, PetMouseGazeMode.Always); TrackBounds();
                remove.Invoke(gaze, null);
                foreach (var joint in joints) result.maximumRestoreError = Mathf.Max(result.maximumRestoreError, Quaternion.Angle(joint.localRotation, originals[joint]));
            }
            if (result.maximumHeadYaw > PetMouseGaze.MaximumHeadYaw + 0.01f || result.maximumHeadPitch > PetMouseGaze.MaximumHeadPitch + 0.01f
                || result.maximumEyeYaw > PetMouseGaze.MaximumEyeYaw + 0.01f || result.maximumEyePitch > PetMouseGaze.MaximumEyePitch + 0.01f) failures.Add("Gaze exceeded anatomical turn limits.");
            if (result.maximumHeadYaw < 2 || result.maximumEyeYaw < 5) failures.Add("Pointer did not produce a visible bounded turn.");
            if (result.maximumRestoreError > 0.08f) failures.Add("Gaze accumulated a resting-pose rotation.");
            float projectionDistance = Mathf.Max(180, camera.pixelHeight * 0.45f);
            Vector2 PointerAt(Vector2 angles) => origin + new Vector2(
                Mathf.Tan(angles.x * Mathf.Deg2Rad) * projectionDistance,
                Mathf.Tan(angles.y * Mathf.Deg2Rad) * projectionDistance);
            Vector2 SettleAt(Vector2 angles)
            {
                gaze.ResetOffsets();
                Vector2 pointer = PointerAt(angles);
                Vector2 previousHead = Vector2.zero, previousEyes = Vector2.zero;
                for (int frame = 0; frame < 240; frame++)
                {
                    Tick(pointer, 1f/60, frame/60f, true, PetMouseGazeMode.Always); TrackBounds();
                    result.maximumHeadStep = Mathf.Max(result.maximumHeadStep, Vector2.Distance(previousHead, gaze.HeadAngles));
                    result.maximumEyeStep = Mathf.Max(result.maximumEyeStep, Vector2.Distance(previousEyes, gaze.EyeAngles));
                    previousHead = gaze.HeadAngles; previousEyes = gaze.EyeAngles;
                    remove.Invoke(gaze, null);
                }
                return gaze.HeadAngles;
            }
            Vector2 near = SettleAt(new Vector2(10, 7));
            Vector2 middle = SettleAt(new Vector2(18, 11));
            Vector2 far = SettleAt(new Vector2(60, 60));
            result.nearHeadYaw = near.x; result.nearHeadPitch = near.y;
            result.middleHeadYaw = middle.x; result.middleHeadPitch = middle.y;
            result.farHeadYaw = far.x; result.farHeadPitch = far.y;
            if (near.x > 3 || near.y > 2) failures.Add("A nearby pointer recruited too much head motion.");
            if (middle.x <= near.x || middle.y <= near.y || far.x <= middle.x * 1.5f || far.y <= middle.y * 1.5f)
                failures.Add("Larger pointer offsets did not progressively recruit the head.");
            if (far.x < 18 || far.y < 10) failures.Add("A distant pointer did not produce the expanded head turn.");
            if (result.maximumHeadStep > 30f/60 + 0.001f || result.maximumEyeStep > 110f/60 + 0.001f)
                failures.Add("Expanded gaze caused a sudden turn instead of respecting the smoothing speed.");
            gaze.ResetOffsets();
            for (int frame = 0; frame < 6; frame++) Tick(PointerAt(new Vector2(60, 0)), 1f/60, frame/60f, true, PetMouseGazeMode.Always);
            result.eyesLedHead = gaze.EyeAngles.x > 1 && gaze.EyeAngles.x > gaze.HeadAngles.x * 2;
            if (!result.eyesLedHead) failures.Add("The eyes did not lead the head after a large pointer move.");
            Tick(origin + Vector2.one * 900, 1f/30, 31, false, PetMouseGazeMode.Always);
            if (gaze.HeadAngles.sqrMagnitude + gaze.EyeAngles.sqrMagnitude > 0.0001f) failures.Add("Guard/action suppression did not clear gaze.");
            // Animator overwrites are authoritative: a newly sampled pose must not
            // be replaced by the stored pre-gaze pose from the previous frame.
            Tick(origin + Vector2.one * 700, 1f/30, 32, true, PetMouseGazeMode.Always);
            Quaternion newAnimation = originals[gaze.HeadBone] * Quaternion.Euler(2,3,4);
            gaze.HeadBone.localRotation = newAnimation;
            remove.Invoke(gaze, null);
            if (Quaternion.Angle(gaze.HeadBone.localRotation, newAnimation) > 0.05f) failures.Add("A fresh animation pose was overwritten by cleanup.");
            gaze.HeadBone.localRotation = originals[gaze.HeadBone]; gaze.ResetOffsets();
            for (int frame = 0; frame < 900; frame++)
            {
                Tick(origin + Vector2.one * 900, 1f/15, frame/15f, true, PetMouseGazeMode.Occasional);
                result.occasionalLooked |= gaze.AttentionWeight > 0.8f;
                result.occasionalRested |= frame > 100 && gaze.AttentionWeight < 0.1f;
                remove.Invoke(gaze, null);
            }
            if (!result.occasionalLooked || !result.occasionalRested) failures.Add("Occasional mode did not alternate attention and rest.");
            for (int frame = 0; frame < 120; frame++) Tick(origin + Vector2.one * 900, 1f/30, 61+frame/30f, true, PetMouseGazeMode.Off);
            if (gaze.HeadAngles.sqrMagnitude + gaze.EyeAngles.sqrMagnitude > 0.0001f) failures.Add("Off mode did not settle gaze to zero.");
            gaze.ResetOffsets();
            foreach (var joint in joints)
            {
                if (Vector3.Distance(joint.localPosition, positions[joint]) > 0.000001f || Vector3.Distance(joint.localScale, scales[joint]) > 0.000001f)
                { failures.Add("Gaze altered bone positions or scale."); break; }
                if (Quaternion.Angle(joint.localRotation, originals[joint]) > 0.08f) { failures.Add("Off mode failed to restore the current animation pose."); break; }
            }
            if (skin != null && Mathf.Abs(skin.GetBlendShapeWeight(0)-27.5f)>0.01f) failures.Add("Gaze altered a speech/expression blendshape.");
        }
        catch (Exception error) { failures.Add(error.GetBaseException().Message); }
        finally { EditorSceneManager.ClosePreviewScene(scene); }
        result.failures = failures.ToArray(); return result;
    }
}

/// <summary>Pure trigger checks and nine-frame skeleton sampling, without moving a live desktop window.</summary>
public static class DesktopPetFastHandReactionChecks
{
    [MenuItem("Desktop Pet/Run Fast Hand Reaction Checks")]
    public static void Run()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
            throw new InvalidOperationException("Exit Play Mode before sampling fast hand reactions.");
        var report = new StringBuilder(CheckLogic());
        var failures = new List<string>();
        var preview = EditorSceneManager.NewPreviewScene();
        var paths = new[] { PetModelSetup.PrefabPath }.Concat(AssetDatabase.FindAssets("t:Prefab", new[] { "Assets/ImportedPets" })
            .Select(AssetDatabase.GUIDToAssetPath).Where(path => path.EndsWith("/DesktopPet.prefab", StringComparison.Ordinal))).Distinct().ToArray();
        try
        {
            foreach (string path in paths)
            {
                GameObject model = null, cameraObject = null;
                try
                {
                    model = UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(path));
                    model.SetActive(false); model.hideFlags = HideFlags.HideAndDontSave;
                    UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(model, preview);
                    cameraObject = new GameObject("FastHandQaCamera", typeof(Camera));
                    cameraObject.hideFlags = HideFlags.HideAndDontSave;
                    UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(cameraObject, preview);
                    CheckRig(model, cameraObject.GetComponent<Camera>(), path, report);
                }
                catch (Exception error) { failures.Add(path + ": " + error.GetBaseException().Message); }
                finally
                {
                    if (model != null) UnityEngine.Object.DestroyImmediate(model);
                    if (cameraObject != null) UnityEngine.Object.DestroyImmediate(cameraObject);
                }
            }
        }
        finally { EditorSceneManager.ClosePreviewScene(preview); }
        report.Insert(0, failures.Count == 0 ? "PASSED\n" : "FAILED\n" + string.Join("\n", failures) + "\n");
        report.AppendLine("LIMIT: skeleton/clock sampling; clothing clearance and native rapid dragging still require rendered review.");
        string folder = Path.GetFullPath(Path.Combine(Application.dataPath, "../../../verification/motion-quality"));
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "fast-hand-reaction-checks.txt"), report.ToString(), new UTF8Encoding(false));
        if (failures.Count == 0) Debug.Log("PET_FAST_HAND_REACTION_CHECKS " + report);
        else Debug.LogError("PET_FAST_HAND_REACTION_CHECKS " + report);
    }

    /// <summary>Deterministic clock QA; safe to call without an active model or Play Mode.</summary>
    public static string CheckLogic()
    {
        foreach (int fps in new[] { 15, 30, 60, 120 })
        {
            float dt = 1f / fps, now = 0;
            var reaction = new PetFastHandReaction(); reaction.Begin(now);
            reaction.Step(1400, 1, true, true, now += dt);
            for (int frame = 0; frame < fps; frame++) reaction.Step(0, 1, false, true, now += dt);
            Require(!reaction.TriggeredThisLead, "A one-frame speed spike triggered at " + fps + " FPS.");
            for (int frame = 0; frame < fps; frame++) reaction.Step(400, 1, true, true, now += dt);
            Require(!reaction.TriggeredThisLead, "Ordinary running triggered at " + fps + " FPS.");
            float fastStart = now, triggeredAt = -1;
            for (int frame = 0; frame < fps; frame++)
            {
                var pose = reaction.Step(700, -1, true, true, now += dt);
                if (pose.Active && triggeredAt < 0) triggeredAt = now;
            }
            Require(triggeredAt > fastStart + PetFastHandReaction.SustainSeconds && triggeredAt < fastStart + .6f,
                "Sustained fast input did not pass the filter/duration gate at " + fps + " FPS.");
            Require(reaction.CurrentPose.direction == -1, "The reaction lost its pull direction.");
            reaction.Step(700, -1, true, false, now += dt);
            Require(!reaction.CurrentPose.Active, "Lifting retained the reaction.");
            for (int frame = 0; frame < fps * 8; frame++) reaction.Step(700, -1, true, true, now += dt);
            Require(!reaction.CurrentPose.Active && reaction.TriggeredThisLead, "Returning to ground rearmed the same hold.");
            reaction.Begin(now);
            for (int frame = 0; frame < fps; frame++) reaction.Step(700, 1, true, true, now += dt);
            Require(reaction.CurrentPose.Active, "A fresh hold after cooldown did not trigger.");
            reaction.Cancel(); Require(!reaction.CurrentPose.Active, "Release retained the pose.");
            reaction.Begin(now);
            for (int frame = 0; frame < fps * 2; frame++) reaction.Step(700, 1, true, true, now += dt);
            Require(!reaction.TriggeredThisLead, "A new hold bypassed the six-second cooldown.");
        }
        var paused = new PetFastHandReaction(); paused.Begin(0);
        paused.Step(700, 1, true, true, .1f); paused.Step(700, 1, true, true, 2f);
        Require(!paused.TriggeredThisLead, "A paused clock counted as sustained movement.");
        paused.Step(float.NaN, 1, true, true, 2.02f);
        Require(!float.IsNaN(paused.FilteredSpeed), "Invalid speed contaminated the filter.");
        var phases = new HashSet<PetFastHandReactionPhase>();
        for (int frame = 0; frame <= 192; frame++)
        {
            var pose = PetFastHandReaction.SamplePose(frame / 100f, 1);
            phases.Add(pose.phase);
            Require(pose.leanDegrees >= 0 && pose.leanDegrees <= 21.001f && pose.braceWeight >= 0
                && pose.braceWeight <= 1 && pose.lookBackWeight >= 0 && pose.lookBackWeight <= 1, "Unbounded pose sample.");
        }
        Require(phases.Count == 6 && !PetFastHandReaction.SamplePose(PetFastHandReaction.Duration, 1).Active,
            "The five reaction phases did not recover to the authored pose.");
        return "PASS clock: 15/30/60/120 FPS, one-frame spike, ordinary run, filter/sustain, direction, airborne cancel, once per hold, cooldown, release, pause and invalid input.\n";
    }

    private static void CheckRig(GameObject model, Camera camera, string path, StringBuilder report)
    {
        var animator = model.GetComponent<Animator>();
        Require(animator != null && animator.runtimeAnimatorController != null, "Missing character Animator.");
        var clips = animator.runtimeAnimatorController.animationClips.Where(clip => clip != null)
            .GroupBy(clip => clip.name).ToDictionary(group => group.Key, group => group.First());
        var all = model.GetComponentsInChildren<Transform>(true);
        var bones = all.Where(bone => !HiddenSource(bone, model.transform)).Select(bone => new
            { key = PetRuntimePoseRetargeter.CanonicalName(bone.name), bone }).Where(item => item.key != null)
            .GroupBy(item => item.key).ToDictionary(group => group.Key, group => group.First().bone);
        var arm = model.GetComponent<PetHandLeadArm>() ?? model.AddComponent<PetHandLeadArm>();
        Require(model.GetComponent<PetCharacterAnimation>() != null, "Missing hand-lead owner.");
        Transform visual = model.transform.Find("VisualModel");
        Require(visual != null && bones.ContainsKey("頭"), "Missing visible torso/head skeleton.");
        Transform[] feet = new[] { bones["足首.L"], bones["足首.R"] };
        Quaternion rest = visual.localRotation;
        clips["Idle"].SampleAnimation(model, 0);
        var bounds = model.GetComponentsInChildren<Renderer>(true).Where(renderer => renderer is SkinnedMeshRenderer)
            .Select(renderer => renderer.bounds).ToArray();
        Bounds framing = bounds.Length > 0 ? bounds[0] : new Bounds(model.transform.position + Vector3.up, Vector3.one * 2);
        foreach (Bounds bound in bounds) framing.Encapsulate(bound);
        camera.orthographic = true; camera.orthographicSize = Mathf.Max(.5f, framing.size.y * .75f);
        camera.pixelRect = new Rect(0, 0, 1024, 1024); camera.aspect = 1;
        camera.transform.position = framing.center + Vector3.back * Mathf.Max(4, framing.size.y * 3);
        camera.transform.rotation = Quaternion.identity;
        float[] times = { 0, .18f, .38f, .60f, .82f, 1.06f, 1.30f, 1.56f, PetFastHandReaction.Duration };
        foreach (string side in new[] { "L", "R" }) foreach (float direction in new[] { -1f, 1f })
        {
            string clipName = "HandLeadRun" + (side == "L" ? "Left" : "Right");
            Require(clips.TryGetValue(clipName, out AnimationClip clip), "Missing " + clipName);
            clip.SampleAnimation(model, 0);
            visual.localRotation = Quaternion.AngleAxis(-direction * 68, Vector3.up) * rest;
            Transform shoulder = bones["腕." + side], elbow = bones["ひじ." + side], wrist = bones["手首." + side];
            float upperLength = Vector3.Distance(shoulder.position, elbow.position), lowerLength = Vector3.Distance(elbow.position, wrist.position);
            float length = upperLength + lowerLength;
            float pixels = Vector2.Distance(camera.WorldToScreenPoint(shoulder.position), camera.WorldToScreenPoint(shoulder.position + camera.transform.right * length));
            float nearReach = Mathf.Min(.85f, Mathf.Max(.3f, Mathf.Abs(upperLength - lowerLength) / length + .08f));
            Vector2 pointer = (Vector2)camera.WorldToScreenPoint(shoulder.position) + new Vector2(direction * nearReach, .06f) * pixels;
            float maximumError = 0; int reachableSamples = 0;
            foreach (float time in times) for (int repeat = 0; repeat < 2; repeat++)
            {
                arm.RestoreSampledPoseForQa();
                clip.SampleAnimation(model, time % clip.length);
                visual.localRotation = Quaternion.AngleAxis(-direction * 68, Vector3.up) * rest;
                var rotations = all.Select(bone => bone.localRotation).ToArray();
                var positions = all.Select(bone => bone.localPosition).ToArray();
                var footPositions = feet.Select(foot => foot.position).ToArray();
                Vector3 origin = model.transform.position;
                Vector2 headBefore = camera.WorldToScreenPoint(bones["頭"].position);
                Require(arm.SampleFastReactionForQa(PetFastHandReaction.SamplePose(time, direction), side, pointer, camera), "Pose sample could not solve the held wrist.");
                Require(Vector3.Distance(origin, model.transform.position) < .000001f, "Reaction moved the actor container.");
                Require(feet.Select((foot, index) => Vector3.Distance(foot.position, footPositions[index])).Max() < .00001f, "Reaction moved a supporting foot.");
                Vector3 pointerTarget = camera.ScreenToWorldPoint(new Vector3(pointer.x, pointer.y,
                    camera.WorldToScreenPoint(shoulder.position).z - length * .025f));
                float pointerReach = Vector3.Distance(pointerTarget, shoulder.position);
                if (pointerReach < length * .97f && pointerReach > Mathf.Abs(upperLength - lowerLength) + length * .02f)
                {
                    Require(arm.TargetError < length * .02f, "Reachable held wrist left the pointer during the reaction.");
                    reachableSamples++;
                }
                maximumError = Mathf.Max(maximumError, arm.TargetError);
                if (Mathf.Abs(time - .6f) < .001f)
                {
                    Vector2 headAfter = camera.WorldToScreenPoint(bones["頭"].position);
                    Require((headAfter.x - headBefore.x) * direction > .1f, "The visible stumble leans opposite the pull direction.");
                }
                arm.RestoreSampledPoseForQa();
                for (int joint = 0; joint < all.Length; joint++)
                {
                    Require(Quaternion.Angle(rotations[joint], all[joint].localRotation) < .06f, "A sampled rotation accumulated: " + all[joint].name);
                    Require(Vector3.Distance(positions[joint], all[joint].localPosition) < .00001f, "A sampled joint position changed.");
                }
            }
            Require(reachableSamples > 0, "No pose sample checked a reachable held pointer.");
            report.AppendLine($"PASS {path}/{side}/direction={direction}: nine keys twice, reachable samples={reachableSamples}, held pointer, pull-side lean, unchanged actor/feet, exact restore; maximum target error={maximumError:F5}m.");
        }
    }

    private static bool HiddenSource(Transform bone, Transform model)
    {
        for (Transform current = bone; current != null && current != model; current = current.parent)
            if (current.name == "__BundledPoseSource") return true;
        return false;
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}

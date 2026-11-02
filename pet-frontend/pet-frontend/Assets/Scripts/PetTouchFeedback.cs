using System;
using System.Collections.Generic;
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

/// <summary>Non-drag body taps become semantic interactions; local lines are explicit fallbacks.</summary>
public sealed class PetTouchFeedback : MonoBehaviour
{
    public int Affinity => Mathf.Clamp(PlayerPrefs.GetInt("desktop_pet.affinity", 20), 0, 100);
    private PetBackendHttpClient client;
    private PetCharacterAnimation character;
    private PetSpeechBubble bubble;
    private float nextTap;
    private bool waiting;
    private string zone;
    private string pendingZone;
    private int pendingAffinity;
    private string pendingLegSide = "R";
    public string LastLegSide { get; private set; } = "R";
    private Vector2 pressedAt;
    private readonly Dictionary<string, int> previousChoices = new Dictionary<string, int>();
    private readonly Dictionary<string, Transform> bones = new Dictionary<string, Transform>();

    private void Start()
    {
        client = GetComponent<PetBackendHttpClient>();
        character = FindAnyObjectByType<PetCharacterAnimation>();
        CacheBones();
        bubble = FindAnyObjectByType<PetSpeechBubble>();
        if (client != null) {
            client.onReplyText.AddListener(OnReply); client.onRequestError.AddListener(OnError);
            client.onChatRequestStarted.AddListener(OnSubmitted);
            client.onRequestCancelled.AddListener(OnCancelled);
        }
    }
    private void Update()
    {
#if UNITY_EDITOR && ENABLE_INPUT_SYSTEM
        if (Mouse.current == null) return;
        Vector2 point = Mouse.current.position.ReadValue();
        if (Mouse.current.leftButton.wasPressedThisFrame) pressedAt = point;
        if (Mouse.current.leftButton.wasReleasedThisFrame && Vector2.Distance(point, pressedAt) < 6) TouchAtScreenPoint(point);
#endif
    }
    public void TouchAtScreenPoint(Vector2 point)
    {
        if (Time.unscaledTime < nextTap || character == null || Camera.main == null) return;
        var windows = GetComponent<TransparentWindowController>();
        if (windows != null && (windows.IsOverUI(point) || windows.ConsumedHandPointer)) return;
        if (!Physics.Raycast(Camera.main.ScreenPointToRay(point), out RaycastHit hit, 100,
                Physics.DefaultRaycastLayers, QueryTriggerInteraction.Collide) || hit.collider.GetComponentInParent<PetCharacterAnimation>() != character) return;
        var behavior = GetComponent<PetBehaviorCoordinator>();
        if (behavior != null && behavior.ConsumeWakeTap()) { nextTap = Time.unscaledTime + .5f; return; }
        behavior?.NotifyLocalInteraction();
        nextTap = Time.unscaledTime + 1.5f;
        zone = ResolveTouchZone(Camera.main, point);
        if (zone == "leg") LastLegSide = ResolveLegSide(Camera.main, point);
        int touchedAffinity = Affinity;
        bool guarded = RequiresGuard(zone, touchedAffinity);
        if (ShouldGainAffinity(zone, touchedAffinity)) GainAffinity();
        if (guarded) character.PlayGuard(zone);
        else if (zone == "leg") character.PlayLegTouch(LegTouchAction(touchedAffinity), LastLegSide);
        else character.PlayAction(ChooseTouchAction(zone, touchedAffinity));
        if (guarded || (zone == "leg" && touchedAffinity < 30)) character.PlayEmotion("neutral");
        if (client == null || client.IsBusy) { ShowFallback(zone, touchedAffinity); return; }
        client.SendInteraction(zone, touchedAffinity);
    }
    public static bool RequiresGuard(string touched, int affinity) => affinity < 30
        && (touched == "chest" || touched == "waist" || touched == "lower_body");
    public static bool ShouldGainAffinity(string touched, int affinity) => !RequiresGuard(touched, affinity) && !(touched == "leg" && affinity < 30);
    public static string LegTouchAction(int affinity) => affinity < 30 ? "leg_retract" : affinity < 70 ? "leg_shift" : "leg_sway";

    private void CacheBones()
    {
        bones.Clear();
        if (character == null) return;
        foreach (Transform bone in character.GetComponentsInChildren<Transform>(true))
            if (!bones.ContainsKey(bone.name)) bones.Add(bone.name, bone);
    }

    public void RebindModel(Transform model)
    {
        character = model != null ? model.GetComponentInChildren<PetCharacterAnimation>(true) : null;
        CacheBones();
        zone = null;
        OnCancelled();
        nextTap = Time.unscaledTime + 0.2f;
    }

    private Vector2 ProjectBone(Camera view, string name, Vector3 fallback)
    {
        Vector3 position = bones.TryGetValue(name, out Transform bone) && bone != null
            ? bone.position : character.transform.TransformPoint(fallback);
        return view.WorldToScreenPoint(position);
    }

    private string ResolveTouchZone(Camera view, Vector2 point)
    {
        // The collider is a broad capsule. Use the visible skeleton's projection for
        // anatomy so the capsule's front surface cannot shift a chest tap up to a shoulder.
        if (bones.Count == 0) CacheBones();
        Vector2 bottom = view.WorldToScreenPoint(character.transform.position);
        Vector2 top = view.WorldToScreenPoint(character.transform.TransformPoint(Vector3.up * 1.72f));
        float height = Mathf.Max(1, Vector2.Distance(bottom, top));
        foreach (string side in new[] { "R", "L" })
        {
            float sign = side == "R" ? -1 : 1;
            Vector2 shoulder = ProjectBone(view, "腕." + side, new Vector3(sign * 0.105f, 1.4f, 0));
            Vector2 elbow = ProjectBone(view, "ひじ." + side, new Vector3(sign * 0.19f, 1.17f, 0));
            Vector2 wrist = ProjectBone(view, "手首." + side, new Vector3(sign * 0.2f, 0.98f, 0));
            // Include the palm beyond the wrist. Hands crossing the torso still count as hands.
            Vector2 palm = wrist + (wrist - elbow).normalized * height * 0.035f;
            if (DistanceToSegment(point, wrist, palm) < height * 0.032f) return "hand";
            if (Vector2.Distance(point, shoulder) < height * 0.025f) return "shoulder";
            if (DistanceToSegment(point, shoulder, elbow) < height * 0.025f
                || DistanceToSegment(point, elbow, wrist) < height * 0.025f) return "arm";
        }
        Vector2 neck = ProjectBone(view, "首", new Vector3(0, 1.45f, 0));
        Vector2 shoulders = (ProjectBone(view, "腕.R", new Vector3(-0.105f, 1.4f, 0))
            + ProjectBone(view, "腕.L", new Vector3(0.105f, 1.4f, 0))) * 0.5f;
        Vector2 spine = ProjectBone(view, "上半身", new Vector3(0, 1.105f, 0));
        Vector2 pelvis = ProjectBone(view, "腰", new Vector3(0, 1.029f, 0));
        return ClassifyTorsoPoint(point, neck, shoulders, spine, pelvis, height);
    }

    public static string ClassifyTorsoPoint(Vector2 point, Vector2 neck, Vector2 shoulders,
        Vector2 spine, Vector2 pelvis, float height)
    {
        height = Mathf.Max(1, height);
        float center = Mathf.Lerp(pelvis.x, neck.x, Mathf.InverseLerp(pelvis.y, neck.y, point.y));
        float side = Mathf.Abs(point.x - center) / height;
        if (point.y >= neck.y + height * 0.015f) return "head";
        if (point.y >= shoulders.y - height * 0.015f) return side > 0.12f ? "arm" : "shoulder";
        if (point.y >= spine.y + height * 0.035f) return side > 0.105f ? "arm" : "chest";
        if (point.y >= pelvis.y + height * 0.01f) return side > 0.105f ? "arm" : "waist";
        if (point.y >= pelvis.y - height * 0.07f && side < 0.085f) return "lower_body";
        return "leg";
    }

    private static float DistanceToSegment(Vector2 point, Vector2 start, Vector2 end)
    {
        Vector2 segment = end - start;
        float along = segment.sqrMagnitude < 0.001f ? 0 : Mathf.Clamp01(Vector2.Dot(point - start, segment) / segment.sqrMagnitude);
        return Vector2.Distance(point, start + along * segment);
    }
    public string ResolveLegSide(Camera view, Vector2 point)
    {
        if (bones.Count == 0) CacheBones();
        float best = float.PositiveInfinity; string result = "R";
        foreach (string side in new[] { "L", "R" })
        {
            float sign = side == "L" ? 1 : -1;
            Vector2 hip = ProjectBone(view, "足." + side, new Vector3(sign * .08f, .90f, 0));
            Vector2 knee = ProjectBone(view, bones.ContainsKey("ひざD." + side) ? "ひざD." + side : "ひざ." + side, new Vector3(sign * .08f, .50f, 0));
            Vector2 foot = ProjectBone(view, bones.ContainsKey("足首D." + side) ? "足首D." + side : "足首." + side, new Vector3(sign * .08f, .10f, 0));
            float distance = Mathf.Min(DistanceToSegment(point, hip, knee), DistanceToSegment(point, knee, foot));
            if (distance < best) { best = distance; result = side; }
        }
        return result;
    }
    public bool TryGetHandSide(Vector2 point, out string side)
    {
        side = null;
        if (character == null || Camera.main == null) return false;
        if (bones.Count == 0) CacheBones();
        Camera view = Camera.main;
        float height = Vector2.Distance(view.WorldToScreenPoint(character.transform.position), view.WorldToScreenPoint(character.transform.TransformPoint(Vector3.up * 1.72f)));
        float best = Mathf.Max(5, height * .034f);
        foreach (string candidate in new[] { "L", "R" })
        {
            if (!bones.TryGetValue("手首." + candidate, out Transform wrist)) continue;
            Vector2 start = view.WorldToScreenPoint(wrist.position);
            Vector2 end;
            if (bones.TryGetValue("中指１." + candidate, out Transform finger)) end = view.WorldToScreenPoint(finger.position);
            else { Vector2 elbow = ProjectBone(view, "ひじ." + candidate, wrist.position); end = start + (start-elbow).normalized * height * .035f; }
            float distance = DistanceToSegment(point, start, end + (end-start) * .4f);
            if (distance < best) { best = distance; side = candidate; }
        }
        return side != null;
    }
    public bool TryGetHandLeadGeometry(string side, out Vector2 shoulder, out float reachPixels)
    {
        shoulder=Vector2.zero;reachPixels=0;
        if(character==null || Camera.main==null)return false;
        if(bones.Count==0)CacheBones();
        if(!bones.TryGetValue("腕."+side,out Transform upper) || !bones.TryGetValue("ひじ."+side,out Transform elbow)
            || !bones.TryGetValue("手首."+side,out Transform wrist))return false;
        float length=Vector3.Distance(upper.position,elbow.position)+Vector3.Distance(elbow.position,wrist.position);
        shoulder=Camera.main.WorldToScreenPoint(upper.position);
        reachPixels=Vector2.Distance(shoulder,Camera.main.WorldToScreenPoint(upper.position+Camera.main.transform.up*length));
        return reachPixels>1;
    }
    private void GainAffinity()
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        long.TryParse(PlayerPrefs.GetString("desktop_pet.affinity_last_gain", "0"), out long previous);
        if (now - previous < 60) return;
        PlayerPrefs.SetInt("desktop_pet.affinity", Mathf.Min(100, Affinity + 1));
        PlayerPrefs.SetString("desktop_pet.affinity_last_gain", now.ToString()); PlayerPrefs.Save();
    }
    private void OnSubmitted(PetChatSubmission submission)
    {
        waiting = submission != null && submission.IsInteraction;
        pendingZone = waiting ? submission.interaction_zone : null;
        pendingAffinity = waiting ? submission.affinity : Affinity;
        pendingLegSide = LastLegSide;
    }
    private void OnReply(string text) { OnCancelled(); }
    private void OnCancelled() { waiting = false; pendingZone = null; }
    private void OnError(string message)
    {
        if (!waiting) return;
        string fallbackZone = pendingZone;
        int fallbackAffinity = pendingAffinity;
        string fallbackSide = pendingLegSide;
        OnCancelled();
        ShowFallback(fallbackZone, fallbackAffinity, fallbackSide);
    }
    private void ShowFallback(string fallbackZone = null, int fallbackAffinity = -1, string fallbackSide = null)
    {
        string[] lines;
        int level = fallbackAffinity < 0 ? Affinity : fallbackAffinity;
        bool close = level >= 70, familiar = level >= 30;
        string touched = fallbackZone ?? zone;
        switch (touched) {
            case "head": lines = close
                ? new[] { "嗯……再待一小会儿。", "头发乱了，你可得负责。", "好吧，今天就让你得意一下。", "这样安安静静的，也很好。" }
                : familiar ? new[] { "唔，差点把我哄困了。", "你是不是也忙累了？", "好啦，知道你来啦。", "今天倒是很温柔嘛。" }
                : new[] { "嗯？……原来是你。", "轻一点，发饰会歪的。", "我刚才有点走神。", "这样打招呼，还真有点突然。" }; break;
            case "shoulder": lines = close
                ? new[] { "不用回头猜也知道是你。", "忙完啦？那歇一会儿。", "好啦，注意力都给你。", "想说什么？我陪你慢慢说。" }
                : familiar ? new[] { "找我呀？", "别急，我听着。", "刚好，我也想歇口气。", "这次又有什么新鲜事？" }
                : new[] { "嗯？有什么事吗？", "啊，你在这里。", "吓了一小跳。", "叫我一声就好，我听得见。" }; break;
            case "hand": lines = close
                ? new[] { "来，击掌。今天也辛苦啦。", "再碰一下，就算约好了。", "偷偷打招呼，被我抓到了吧。", "好啦，给你一点好运。" }
                : familiar ? new[] { "来，碰个拳。", "是在给我打气吗？", "这下精神了，谢谢你。", "嗯，今天也一起加油。" }
                : new[] { "你好呀。", "这是……要击掌？", "好，轻轻碰一下。", "很高兴见到你。" }; break;
            case "arm": lines = close
                ? new[] { "怎么，又想拉我去散步？", "好好好，我陪你。", "不用拉啦，我没走远。", "被你这么一闹，倒是不困了。" }
                : familiar ? new[] { "等我伸个懒腰。", "有小秘密要讲？", "我在这儿呢。", "想歇一会儿就直说嘛。" }
                : new[] { "怎么啦？", "慢一点，不着急。", "有话可以直接说。", "嗯，我听着。" }; break;
            case "chest":
            case "waist":
            case "lower_body": lines = familiar
                ? new[] { "停一下，这里还是别碰哦。", "想叫我就直接叫呀。", "换成击掌吧，这个我愿意。", "好啦，我还没答应呢。" }
                : new[] { "等一下，这样我不太舒服。", "这里先别碰，好吗？", "先停一下，留一点距离吧。", "诶，别这样。叫我一声就好。" }; break;
            case "leg": lines = close
                ? new[] { "好啦，陪你活动一下。", "你也起来走走呀。", "忙完了吗？一起歇口气。", "嗯，这样轻松多了。" }
                : familiar ? new[] { "站久了，正好活动一下。", "想聊天呀？我在听。", "嗯，换个姿势。", "慢一点，我先站稳啦。" }
                : new[] { "先别碰，好吗？", "停一下，让我站稳。", "我不太习惯这样，先别闹啦。", "先停一下，有事叫我就好。" }; break;
            default: lines = close
                ? new[] { "知道啦，陪你活动一下。", "你也起来走走，别光催我。", "好，看我伸个懒腰。", "再坐下去，就要一起犯困了。" }
                : familiar ? new[] { "站久了，正好活动一下。", "要不要一起休息一会儿？", "嗯，换个姿势。", "你也记得起来伸伸腰。" }
                : new[] { "嗯？我站得很稳。", "小心一点哦。", "是想叫我走走吗？", "正好，稍微活动一下。" }; break;
        }
        string text = Choose("line:" + touched + ":" + (close ? 2 : familiar ? 1 : 0), lines);
        if (RequiresGuard(touched, level)) { character?.PlayGuard(touched); character?.PlayEmotion("neutral"); }
        else if (touched == "leg") { character?.PlayLegTouch(LegTouchAction(level), fallbackSide ?? LastLegSide); if (level < 30) character?.PlayEmotion("neutral"); }
        if (bubble == null) bubble = FindAnyObjectByType<PetSpeechBubble>();
        if (client != null && client.SpeechEnabled) client.SpeakReply(text);
        else bubble?.ShowMessage(text);
        if (client != null) client.onServiceStatus.Invoke("本地互动 · 模型回复暂不可用。");
    }
    private string ChooseTouchAction(string touched, int affinity)
    {
        if (RequiresGuard(touched, affinity)) return "guard";
        if (touched == "leg") return LegTouchAction(affinity);
        string[] choices;
        switch (touched) {
            case "head": choices = affinity >= 70 ? new[] { "shy", "nod", "tilt_head" } : new[] { "tilt_head", "nod" }; break;
            case "shoulder": choices = new[] { "look_around", "nod", "tilt_head" }; break;
            case "hand": choices = affinity >= 30 ? new[] { "wave", "cheer", "nod" } : new[] { "wave", "nod" }; break;
            case "arm": choices = new[] { "tilt_head", "nod", "stretch" }; break;
            case "chest":
            case "waist":
            case "lower_body": choices = new[] { "tilt_head", "nod", "idle" }; break;
            default: choices = new[] { "stretch", "look_around", "tilt_head" }; break;
        }
        return Choose("action:" + touched, choices);
    }
    private string Choose(string key, string[] choices)
    {
        int selected;
        if (choices.Length > 1 && previousChoices.TryGetValue(key, out int previous) && previous < choices.Length) {
            selected = UnityEngine.Random.Range(0, choices.Length - 1);
            if (selected >= previous) selected++;
        } else selected = UnityEngine.Random.Range(0, choices.Length);
        previousChoices[key] = selected;
        return choices[selected];
    }
    public static string ZoneLabel(string value)
    {
        switch (value) { case "head": return "头部"; case "shoulder": return "肩膀"; case "arm": return "手臂";
            case "hand": return "手部"; case "chest": return "胸前"; case "waist": return "腰腹";
            case "lower_body": return "下腹"; default: return "腿部"; }
    }
    private void OnDestroy()
    {
        if (client == null) return;
        client.onReplyText.RemoveListener(OnReply); client.onRequestError.RemoveListener(OnError);
        client.onChatRequestStarted.RemoveListener(OnSubmitted); client.onRequestCancelled.RemoveListener(OnCancelled);
    }
}

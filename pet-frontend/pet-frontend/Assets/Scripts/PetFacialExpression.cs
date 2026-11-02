using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

/// <summary>One face mixer owns expression and eyelid shapes. Speech keeps its own A shape;
/// mouth expressions yield to that amplitude so smiling does not clamp spoken syllables.</summary>
public sealed class PetFacialExpression
{
    private enum Feature { Blink, BlinkLeft, BlinkRight, BrowHappy, BrowSad, BrowAngry, BrowUp, BrowDoubt,
        BrowShy, EyeHappy, EyeNarrow, EyeWide, EyeSad, MouthSmile, MouthFrown, MouthRound, Blush, Count }
    private sealed class Channel { public SkinnedMeshRenderer renderer; public int index; public Feature feature; public float weight; public float scale = 1; }
    private sealed class Destination { public SkinnedMeshRenderer renderer; public int index; public readonly List<Channel> channels = new List<Channel>(); }
    private readonly List<Channel> channels = new List<Channel>();
    private readonly Dictionary<(SkinnedMeshRenderer, int), Destination> destinations = new Dictionary<(SkinnedMeshRenderer, int), Destination>();
    private readonly float[] current = new float[(int)Feature.Count], target = new float[(int)Feature.Count];
    private string emotion = "neutral";
    private float emotionUntil, nextBlink, blinkStarted = -100;
    private bool secondBlink;
    public string Emotion => emotion;
    public int ChannelCount => channels.Count;

    public PetFacialExpression(SkinnedMeshRenderer[] renderers)
    {
        if (renderers != null) foreach (SkinnedMeshRenderer skin in renderers) {
            if (skin == null || skin.sharedMesh == null) continue;
            Mesh mesh = skin.sharedMesh;
            var names = new string[mesh.blendShapeCount];
            for (int i = 0; i < names.Length; i++) names[i] = Normalize(mesh.GetBlendShapeName(i));
            bool combinedBlink = Add(skin, names, Feature.Blink, "まばたき", "blink", "eyeblink", "*aniblink");
            // A generic "Close" can also close a mouth or move the whole face.
            // Only an authored, nonzero eyelid/lash part may use it as a blink.
            if (!combinedBlink && HasEyelidClose(skin, names)) combinedBlink = Add(skin, names, Feature.Blink, "close");
            if (!combinedBlink) {
                Add(skin, names, Feature.BlinkLeft, "ウィンク", "blinkleft", "eyeblinkleft", "blinkl");
                Add(skin, names, Feature.BlinkRight, "ウィンク右", "blinkright", "eyeblinkright", "blinkr");
            }
            // Use the full-face 00 variant, never stack the 00/01/02 preset alternatives.
            Add(skin, names, Feature.BrowHappy, "にこり", "*brow00gentle", "browssoft", "上");
            Add(skin, names, Feature.BrowSad, "困る", "悲しい", "*brow00trouble", "browinnerup", "browsworry");
            Add(skin, names, Feature.BrowAngry, "怒り", "真面目", "*brow00angry", "browdownleft", "browdownright");
            Add(skin, names, Feature.BrowUp, "上", "*brow00surprise", "*brow00up", "browouterupleft", "browouterupright");
            Add(skin, names, Feature.BrowDoubt, "*brow00doubt", "真面目左", "困る右", "怒り右", "困る", "browsworry");
            Add(skin, names, Feature.BrowShy, "*brow00shy", "にこり", "困る", "browssoft");
            Add(skin, names, Feature.EyeHappy, "笑い", "慈愛", "なごみ", "*eye00gentle01", "eyesmile", "eyesquintleft", "eyesquintright");
            Add(skin, names, Feature.EyeNarrow, "じと目", "*eye00doubt01", "*eye00angry01", "eyesquintleft", "eyesquintright");
            Add(skin, names, Feature.EyeWide, "びっくり", "*eye00surprise01", "eyewideleft", "eyewideright");
            Add(skin, names, Feature.EyeSad, "おとなしい", "*eye00sweat01", "じと目");
            Add(skin, names, Feature.MouthSmile, "口角上げ", "にやり", "*mouth00smile01", "smile", "mouthsmileleft", "mouthsmileright");
            Add(skin, names, Feature.MouthFrown, "口角下げ", "ムッ", "*mouth00worry01", "*mouth00down", "mouthfrownleft", "mouthfrownright", "frown");
            Add(skin, names, Feature.MouthRound, "お", "*mouth00o", "visemeoh", "mouthfunnel");
            Add(skin, names, Feature.Blush, "照れ", "脸红", "赤面", "blush");
        }
        nextBlink = Time.unscaledTime + UnityEngine.Random.Range(1.8f, 3.6f);
    }

    private static bool HasEyelidClose(SkinnedMeshRenderer skin, string[] names)
    {
        int index = Array.IndexOf(names, "close");
        if (index < 0 || skin.sharedMesh.GetBlendShapeFrameCount(index) == 0) return false;
        Mesh mesh = skin.sharedMesh;
        Material[] materials = skin.sharedMaterials;
        var eyelidVertices = new HashSet<int>();
        for (int submesh = 0; submesh < Mathf.Min(materials.Length, mesh.subMeshCount); submesh++) {
            Material material = materials[submesh];
            if (material == null) continue;
            // Material names often end in Blender's ".001"; preserve the full
            // name rather than applying the blend-shape namespace stripping.
            string name = material.name.Normalize(NormalizationForm.FormKC).ToLowerInvariant();
            if (name.Contains("eyelid") || name.Contains("lash") || name.Contains("_lid") || name.StartsWith("lid", StringComparison.Ordinal)
                || name.Contains("睫毛") || name.Contains("まつげ") || name.Contains("まつ毛") || name.Contains("まぶた") || name.Contains("目蓋")) {
                foreach (int vertex in mesh.GetIndices(submesh)) eyelidVertices.Add(vertex);
            }
        }
        if (eyelidVertices.Count == 0) return false;
        var offsets = new Vector3[mesh.vertexCount];
        mesh.GetBlendShapeFrameVertices(index, mesh.GetBlendShapeFrameCount(index) - 1, offsets, null, null);
        bool moved = false;
        for (int vertex = 0; vertex < offsets.Length; vertex++) if (offsets[vertex].sqrMagnitude > 1e-12f) {
            if (!eyelidVertices.Contains(vertex)) return false;
            moved = true;
        }
        return moved;
    }

    private bool Add(SkinnedMeshRenderer renderer, string[] names, Feature feature, params string[] aliases)
    {
        foreach (string alias in aliases) {
            bool suffix = alias.StartsWith("*", StringComparison.Ordinal);
            string expected = Normalize(suffix ? alias.Substring(1) : alias);
            for (int index = 0; index < names.Length; index++) {
                if (!(suffix ? names[index].EndsWith(expected, StringComparison.Ordinal) : names[index] == expected)) continue;
                AddChannel(renderer, index, feature, feature == Feature.BrowHappy && expected == "上" ? 0.32f : 1);
                // ARKit represents paired features separately; consume both sides if present.
                if (!expected.EndsWith("left", StringComparison.Ordinal) && !expected.EndsWith("right", StringComparison.Ordinal)) return true;
                string opposite = expected.EndsWith("left", StringComparison.Ordinal)
                    ? expected.Substring(0, expected.Length - 4) + "right" : expected.Substring(0, expected.Length - 5) + "left";
                int partner = Array.IndexOf(names, opposite);
                if (partner >= 0) AddChannel(renderer, partner, feature);
                return true;
            }
        }
        return false;
    }

    private void AddChannel(SkinnedMeshRenderer renderer, int index, Feature feature, float scale = 1)
    {
        var channel = new Channel { renderer = renderer, index = index, feature = feature, scale = scale };
        channels.Add(channel);
        var key = (renderer, index);
        if (!destinations.TryGetValue(key, out Destination destination)) {
            destination = new Destination { renderer = renderer, index = index }; destinations.Add(key, destination);
        }
        destination.channels.Add(channel);
    }

    private static string Normalize(string value)
    {
        string shortName = value.Substring(value.LastIndexOf('.') + 1);
        return shortName.Normalize(NormalizationForm.FormKC).Replace("_", "").Replace("-", "").Replace(" ", "").ToLowerInvariant();
    }

    public void SetEmotion(string value, float now)
    {
        switch ((value ?? "").Trim().ToLowerInvariant()) {
            case "happy": case "joy": case "cheerful": emotion = "happy"; break;
            case "excited": case "cheer": emotion = "excited"; break;
            case "curious": case "confused": case "doubt": emotion = "curious"; break;
            case "thinking": case "think": emotion = "thinking"; break;
            case "sad": case "worried": emotion = "sad"; break;
            case "surprised": case "surprise": emotion = "surprised"; break;
            case "shy": case "embarrassed": emotion = "shy"; break;
            case "angry": case "guard": emotion = "angry"; break;
            default: emotion = "neutral"; break;
        }
        emotionUntil = now + (emotion == "surprised" ? 3.5f : 7f);
    }

    public void SetAction(string state, float now)
    {
        switch (state) {
            case "Happy": case "Wave": SetEmotion("happy", now); break;
            case "Cheer": SetEmotion("excited", now); break;
            case "Think": SetEmotion("thinking", now); break;
            case "TiltHead": case "LookAround": SetEmotion("curious", now); break;
            case "Sad": SetEmotion("sad", now); break;
            case "Surprised": SetEmotion("surprised", now); break;
            case "Shy": SetEmotion("shy", now); break;
            case "Guard": case "GuardLower": case "AngryStomp": SetEmotion("angry", now); break;
        }
    }

    public void Tick(float now, float deltaTime, float speechAmplitude, bool guarding, float sleepClosure = 0, float yawn = 0)
    {
        if (now >= emotionUntil) emotion = "neutral";
        Profile(guarding ? "angry" : emotion);
        if (yawn > 0) target[(int)Feature.MouthRound] = Mathf.Max(target[(int)Feature.MouthRound], Mathf.Clamp01(yawn) * 35);
        float blend = 1 - Mathf.Exp(-Mathf.Max(0, deltaTime) * 9);
        for (int i = 0; i < current.Length; i++) current[i] = Mathf.Lerp(current[i], target[i], blend);
        if (now >= nextBlink) {
            blinkStarted = now;
            if (!secondBlink && UnityEngine.Random.value < 0.16f) { secondBlink = true; nextBlink = now + 0.29f; }
            else { secondBlink = false; nextBlink = now + UnityEngine.Random.Range(3.1f, 5.8f); }
        }
        float t = now - blinkStarted;
        float blink = t < 0 ? 0 : t < 0.065f ? Mathf.SmoothStep(0, 1, t / 0.065f)
            : t < 0.095f ? 1 : t < 0.22f ? 1 - Mathf.SmoothStep(0, 1, (t - 0.095f) / 0.125f) : 0;
        Apply(Mathf.Max(blink, Mathf.Clamp01(sleepClosure)), speechAmplitude);
    }

    /// <summary>Deterministic expression sampling for editor thumbnails, without changing a saved mesh.</summary>
    public void Sample(string value, float blink = 0, float speechAmplitude = 0)
    {
        Profile(value); Array.Copy(target, current, current.Length); Apply(Mathf.Clamp01(blink), speechAmplitude);
    }

    private void Profile(string value)
    {
        Array.Clear(target, 0, target.Length);
        switch (value) {
            case "happy":
                Set(Feature.BrowHappy, 50); Set(Feature.EyeHappy, 45); Set(Feature.MouthSmile, 80); break;
            case "excited":
                Set(Feature.BrowHappy, 42); Set(Feature.BrowUp, 30); Set(Feature.EyeHappy, 58); Set(Feature.MouthSmile, 85); break;
            case "curious":
                Set(Feature.BrowDoubt, 68); Set(Feature.BrowUp, 35); Set(Feature.EyeWide, 32); Set(Feature.MouthRound, 15); break;
            case "thinking":
                Set(Feature.BrowDoubt, 48); Set(Feature.BrowAngry, 20); Set(Feature.EyeNarrow, 45); Set(Feature.MouthFrown, 16); break;
            case "sad":
                Set(Feature.BrowSad, 64); Set(Feature.EyeSad, 36); Set(Feature.EyeNarrow, 15); Set(Feature.MouthFrown, 48); break;
            case "surprised":
                Set(Feature.BrowUp, 75); Set(Feature.EyeWide, 70); Set(Feature.MouthRound, 50); break;
            case "shy":
                Set(Feature.BrowShy, 48); Set(Feature.EyeHappy, 14); Set(Feature.EyeNarrow, 13); Set(Feature.MouthSmile, 30); Set(Feature.Blush, 65); break;
            case "angry":
                Set(Feature.BrowAngry, 62); Set(Feature.EyeNarrow, 35); Set(Feature.MouthFrown, 38); break;
            default:
                Set(Feature.BrowHappy, 5); Set(Feature.MouthSmile, 7); break;
        }
    }

    private void Set(Feature feature, float weight) => target[(int)feature] = weight;

    private void Apply(float blink, float speechAmplitude)
    {
        float speech = Mathf.Clamp01(speechAmplitude / 65);
        foreach (Channel channel in channels) {
            float weight = current[(int)channel.feature] * channel.scale;
            if (channel.feature <= Feature.BlinkRight) weight = blink * 100;
            else if (channel.feature >= Feature.EyeHappy && channel.feature <= Feature.EyeSad) weight *= 1 - blink;
            else if (channel.feature == Feature.MouthRound) weight *= 1 - speech;
            else if (channel.feature == Feature.MouthSmile || channel.feature == Feature.MouthFrown) weight *= 1 - speech * 0.6f;
            channel.weight = Mathf.Clamp(weight, 0, 100);
        }
        // Fallbacks may share one shape (e.g. MMD shy and happy eyebrows).
        // Resolve those weights once rather than letting the last feature erase the first.
        foreach (Destination destination in destinations.Values) {
            if (destination.renderer == null) continue;
            float weight = 0;
            foreach (Channel channel in destination.channels) weight = Mathf.Max(weight, channel.weight);
            destination.renderer.SetBlendShapeWeight(destination.index, weight);
        }
    }

    public string[] DescribeChannels()
    {
        var result = new List<string>();
        foreach (Channel channel in channels) if (channel.renderer != null && channel.renderer.sharedMesh != null)
            result.Add(channel.renderer.name + ": " + channel.feature + " = " + channel.renderer.sharedMesh.GetBlendShapeName(channel.index));
        return result.ToArray();
    }

    public void Reset()
    {
        emotion = "neutral"; emotionUntil = 0; blinkStarted = -100;
        nextBlink = Time.unscaledTime + UnityEngine.Random.Range(2, 4); secondBlink = false;
        Array.Clear(current, 0, current.Length); Array.Clear(target, 0, target.Length);
        foreach (Channel channel in channels) if (channel.renderer != null) channel.renderer.SetBlendShapeWeight(channel.index, 0);
    }
}

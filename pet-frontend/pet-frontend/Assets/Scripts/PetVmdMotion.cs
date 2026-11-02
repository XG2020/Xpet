using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;

/// <summary>Bounded VMD 0002 bone-motion reader. No Editor, converter, script or native DLL is involved.</summary>
public sealed class PetVmdMotion
{
    public const int MaximumBytes = 32 * 1024 * 1024;
    public const uint MaximumFrame = 5400; // Three minutes at VMD's fixed 30 FPS.
    public sealed class Key
    {
        public uint frame;
        public Vector3 position;
        public Quaternion rotation;
        public byte[] interpolation;
    }
    public readonly Dictionary<string, Key[]> tracks = new Dictionary<string, Key[]>(StringComparer.Ordinal);
    public float Duration { get; private set; }
    public int FrameCount { get; private set; }
    public string ModelName { get; private set; }

    public static PetVmdMotion Read(string path) => Read(File.ReadAllBytes(path));

    public static PetVmdMotion Read(byte[] bytes)
    {
        if (bytes == null || bytes.Length < 54 || bytes.Length > MaximumBytes)
            throw new InvalidDataException("VMD 文件需完整且不超过 32 MB。");
        using (var reader = new BinaryReader(new MemoryStream(bytes, false)))
        {
            string signature = Encoding.ASCII.GetString(reader.ReadBytes(30)).TrimEnd('\0');
            if (signature != "Vocaloid Motion Data 0002") throw new InvalidDataException("仅支持 VMD 0002 骨骼动作文件。");
            Encoding japanese;
            try { japanese = Encoding.GetEncoding(932); }
            catch (Exception) { throw new InvalidDataException("运行组件缺少日文编码支持，请使用完整发行包。"); }
            var result = new PetVmdMotion { ModelName = Decode(reader.ReadBytes(20), japanese) };
            uint count = reader.ReadUInt32();
            if (count == 0 || count > 200000 || count > (bytes.Length - reader.BaseStream.Position) / 111)
                throw new InvalidDataException("VMD 没有可用骨骼帧，或骨骼数据已损坏。");
            var collected = new Dictionary<string, Dictionary<uint, Key>>(StringComparer.Ordinal);
            uint lastFrame = 0;
            for (uint index = 0; index < count; index++)
            {
                string name = CanonicalBone(Decode(reader.ReadBytes(15), japanese));
                uint frame = reader.ReadUInt32();
                float x = reader.ReadSingle(), y = reader.ReadSingle(), z = reader.ReadSingle();
                float qx = reader.ReadSingle(), qy = reader.ReadSingle(), qz = reader.ReadSingle(), qw = reader.ReadSingle();
                byte[] interpolation = reader.ReadBytes(64);
                if (frame > MaximumFrame || !Finite(x) || !Finite(y) || !Finite(z) || !Finite(qx) || !Finite(qy) || !Finite(qz) || !Finite(qw)
                    || Mathf.Max(Mathf.Abs(x), Mathf.Abs(y), Mathf.Abs(z)) > 10000)
                    throw new InvalidDataException("动作含无效帧、坐标或超过三分钟的时长。");
                float magnitude = Mathf.Sqrt(qx * qx + qy * qy + qz * qz + qw * qw);
                if (!Finite(magnitude) || magnitude < 0.00001f) throw new InvalidDataException("动作含无效关节旋转。");
                if (string.IsNullOrWhiteSpace(name)) continue;
                if (!collected.TryGetValue(name, out var frames)) collected.Add(name, frames = new Dictionary<uint, Key>());
                frames[frame] = new Key { frame = frame, position = new Vector3(x, y, -z),
                    rotation = new Quaternion(-qx / magnitude, -qy / magnitude, qz / magnitude, qw / magnitude), interpolation = interpolation };
                lastFrame = Math.Max(lastFrame, frame);
            }
            foreach (var track in collected) result.tracks.Add(track.Key, track.Value.OrderBy(item => item.Key).Select(item => item.Value).ToArray());
            if (result.tracks.Count == 0 || lastFrame == 0) throw new InvalidDataException("文件没有可播放的骨骼动画。");
            result.Duration = lastFrame / 30f;
            result.FrameCount = (int)count;
            return result;
        }
    }

    public static void Sample(Key[] keys, float seconds, out Vector3 position, out Quaternion rotation)
    {
        float frame = Mathf.Max(0, seconds) * 30;
        int left = 0, right = keys.Length - 1;
        if (frame <= keys[0].frame) { position = keys[0].position; rotation = keys[0].rotation; return; }
        if (frame >= keys[right].frame) { position = keys[right].position; rotation = keys[right].rotation; return; }
        while (right - left > 1) { int middle = (left + right) / 2; if (keys[middle].frame <= frame) left = middle; else right = middle; }
        Key a = keys[left], b = keys[right];
        float fraction = (frame - a.frame) / (b.frame - a.frame);
        // VMD stores incoming interpolation on the destination frame, independently for X/Y/Z/rotation.
        position = new Vector3(Mathf.Lerp(a.position.x, b.position.x, Bezier(b.interpolation, 0, fraction)),
            Mathf.Lerp(a.position.y, b.position.y, Bezier(b.interpolation, 1, fraction)),
            Mathf.Lerp(a.position.z, b.position.z, Bezier(b.interpolation, 2, fraction)));
        rotation = Quaternion.Slerp(a.rotation, b.rotation, Bezier(b.interpolation, 3, fraction));
    }

    public static float Bezier(byte[] values, int channel, float x)
    {
        float x1 = Mathf.Clamp01(values[channel] / 127f), y1 = Mathf.Clamp01(values[channel + 4] / 127f);
        float x2 = Mathf.Clamp01(values[channel + 8] / 127f), y2 = Mathf.Clamp01(values[channel + 12] / 127f);
        float low = 0, high = 1;
        for (int iteration = 0; iteration < 14; iteration++)
        {
            float t = (low + high) * 0.5f, u = 1 - t;
            float candidate = 3 * u * u * t * x1 + 3 * u * t * t * x2 + t * t * t;
            if (candidate < x) low = t; else high = t;
        }
        float v = (low + high) * 0.5f, inverse = 1 - v;
        return 3 * inverse * inverse * v * y1 + 3 * inverse * v * v * y2 + v * v * v;
    }

    public static string CanonicalBone(string value)
    {
        value = value.Trim().Replace("肘", "ひじ").Replace("膝", "ひざ").Replace("腕捻", "腕捩").Replace("手捻", "手捩")
            .Replace("人差し指", "人指").Replace("人差指", "人指");
        if (value.StartsWith("左", StringComparison.Ordinal)) return value.Substring(1) + ".L";
        if (value.StartsWith("右", StringComparison.Ordinal)) return value.Substring(1) + ".R";
        return value;
    }
    private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    private static string Decode(byte[] value, Encoding encoding) { int length = Array.IndexOf(value, (byte)0); return encoding.GetString(value, 0, length < 0 ? value.Length : length); }
}

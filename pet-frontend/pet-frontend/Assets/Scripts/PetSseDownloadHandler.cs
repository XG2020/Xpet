using System;
using System.Collections.Concurrent;
using System.Text;
using UnityEngine.Networking;

/// <summary>Incrementally decodes UTF-8 and complete SSE data frames, never partial JSON.</summary>
public sealed class PetSseDownloadHandler : DownloadHandlerScript
{
    private readonly Decoder decoder = Encoding.UTF8.GetDecoder();
    private readonly StringBuilder line = new StringBuilder();
    private readonly StringBuilder data = new StringBuilder();
    private readonly StringBuilder responsePrefix = new StringBuilder();
    private readonly ConcurrentQueue<string> frames = new ConcurrentQueue<string>();
    private int totalBytes;
    public string Failure { get; private set; }
    public string ErrorBody => responsePrefix.ToString();
    public PetSseDownloadHandler() : base(new byte[8192]) { }
    public bool TryRead(out string frame) => frames.TryDequeue(out frame);

    protected override bool ReceiveData(byte[] bytes, int count)
    {
        if (bytes == null || count <= 0) return true;
        totalBytes += count;
        if (totalBytes > 2 * 1024 * 1024) { Failure = "回复超过可接收大小。"; return false; }
        var chars = new char[count + 2];
        int characterCount = decoder.GetChars(bytes, 0, count, chars, 0, false);
        if (responsePrefix.Length < 8192)
            responsePrefix.Append(chars, 0, Math.Min(characterCount, 8192 - responsePrefix.Length));
        for (int i = 0; i < characterCount; i++) {
            char value = chars[i];
            if (value == '\n') { ProcessLine(line.ToString().TrimEnd('\r')); line.Clear(); }
            else line.Append(value);
        }
        return true;
    }
    private void ProcessLine(string value)
    {
        if (value.Length == 0) {
            if (data.Length > 0) { frames.Enqueue(data.ToString()); data.Clear(); }
        } else if (value.StartsWith("data:", StringComparison.Ordinal)) {
            if (data.Length > 0) data.Append('\n');
            data.Append(value.Substring(5).TrimStart(' '));
        }
    }
    protected override void CompleteContent()
    {
        if (line.Length > 0) ProcessLine(line.ToString().TrimEnd('\r'));
        ProcessLine("");
    }
}

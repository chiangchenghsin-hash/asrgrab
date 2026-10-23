using System.Text;
using System.Text.Json.Serialization;

namespace AsrGrab;

public sealed class Segment
{
    [JsonPropertyName("start_ms")] public int StartMs { get; set; }
    [JsonPropertyName("end_ms")] public int EndMs { get; set; }
    [JsonPropertyName("speaker")] public string? Speaker { get; set; }
    [JsonPropertyName("text")] public string Text { get; set; } = "";

    public object Public() => new
    {
        start_ms = StartMs,
        end_ms = EndMs,
        speaker = Speaker,
        text = Text,
    };

    public string ToLine()
    {
        var ts = $"[{Out.FmtMs(StartMs)} - {Out.FmtMs(EndMs)}]";
        return string.IsNullOrEmpty(Speaker) ? $"{ts} {Text}" : $"{ts} {Speaker}：{Text}";
    }
}

public sealed class TranscriptWriter : IDisposable
{
    private readonly StreamWriter _md;
    private readonly Dictionary<string, object?> _meta;
    private readonly List<Segment> _segments = new();

    public string MdPath { get; }
    public string JsonPath { get; }
    public int SegmentCount => _segments.Count;

    public TranscriptWriter(string mdPath, Dictionary<string, object?> meta)
    {
        MdPath = Path.GetFullPath(mdPath);
        JsonPath = Path.ChangeExtension(MdPath, ".json");
        _meta = meta;

        var dir = Path.GetDirectoryName(MdPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        _md = new StreamWriter(MdPath, false, new UTF8Encoding(false)) { AutoFlush = true };
        WriteHeader();
        DumpJson();
    }

    private void WriteHeader()
    {
        _md.WriteLine("# 视频转写稿");
        _md.WriteLine();
        _md.WriteLine($"> 生成时间：{Str(_meta, "created_at")}");
        _md.WriteLine($"> 引擎：{Str(_meta, "engine")}");
        if (_meta.TryGetValue("source", out var src) && src is AudioSource s)
            _md.WriteLine($"> 音源：{s.Name}（{(s.Kind == "loopback" ? "系统声音" : "输入设备")}）");
        _md.WriteLine();
        _md.WriteLine("## 转写记录");
        _md.WriteLine();
    }

    private static string Str(Dictionary<string, object?> d, string key)
        => d.TryGetValue(key, out var v) && v != null ? v.ToString() ?? "" : "";

    public void Append(IEnumerable<Segment> segments)
    {
        var added = 0;
        foreach (var seg in segments)
        {
            _md.WriteLine(seg.ToLine());
            _segments.Add(seg);
            added++;
        }

        if (added == 0) return;
        _md.WriteLine();
        _md.Flush();
        DumpJson();
    }

    private void DumpJson()
    {
        JsonIo.Write(JsonPath, new Dictionary<string, object?>
        {
            ["meta"] = _meta,
            ["segment_count"] = _segments.Count,
            ["segments"] = _segments,
        }, pretty: true);
    }

    public void Dispose()
    {
        try { _md.Dispose(); } catch { }
        DumpJson();
    }
}

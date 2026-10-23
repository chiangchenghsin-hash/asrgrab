using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AsrGrab;

public static class ExitCodes
{
    public const int Ok = 0;
    public const int Internal = 1;
    public const int Usage = 2;
    public const int Source = 3;
    public const int Busy = 4;
    public const int Dependency = 5;
    public const int Engine = 6;
    public const int Audio = 7;
}

public sealed class CliError : Exception
{
    public int ExitCode { get; }
    public string Code { get; }
    public Dictionary<string, object?> Extra { get; }

    public CliError(int exitCode, string code, string message, Dictionary<string, object?>? extra = null)
        : base(message)
    {
        ExitCode = exitCode;
        Code = code;
        Extra = extra ?? new Dictionary<string, object?>();
    }
}

public static class JsonIo
{
    public static readonly JsonSerializerOptions Compact = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static readonly JsonSerializerOptions Pretty = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Serialize(object? value, bool pretty = false)
        => JsonSerializer.Serialize(value, pretty ? Pretty : Compact);

    public static void Write(string path, object? value, bool pretty = false)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, Serialize(value, pretty), new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
    }

    public static T? Read<T>(string path)
    {
        try
        {
            if (!File.Exists(path)) return default;
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path, Encoding.UTF8), Compact);
        }
        catch
        {
            return default;
        }
    }
}

public static class Out
{
    public static bool Human;

    public static void Json(object? value) => Console.Out.WriteLine(JsonIo.Serialize(value));
    public static void Event(object? value) => Json(value);

    public static void H(string text = "")
    {
        if (Human) Console.Error.WriteLine(text);
    }

    public static void Error(CliError e)
    {
        var err = new Dictionary<string, object?>
        {
            ["code"] = e.Code,
            ["message"] = e.Message,
        };
        foreach (var kv in e.Extra) err[kv.Key] = kv.Value;
        Json(new Dictionary<string, object?> { ["ok"] = false, ["error"] = err });
    }

    public static string FmtMs(int ms)
    {
        var s = Math.Max(0, ms) / 1000.0;
        return $"{(int)(s / 60):00}:{(int)(s % 60):00}";
    }
}

public sealed class ArgParser
{
    private readonly Dictionary<string, string?> _opts = new(StringComparer.OrdinalIgnoreCase);

    public List<string> Positional { get; } = new();

    public ArgParser(IEnumerable<string> args)
    {
        var list = args.ToList();
        for (var i = 0; i < list.Count; i++)
        {
            var a = list[i];
            if (a == "--")
            {
                for (var j = i + 1; j < list.Count; j++) Positional.Add(list[j]);
                break;
            }

            if (a.StartsWith("--", StringComparison.Ordinal))
            {
                var body = a[2..];
                var eq = body.IndexOf('=');
                if (eq >= 0)
                {
                    _opts[body[..eq]] = body[(eq + 1)..];
                }
                else if (i + 1 < list.Count && !list[i + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    _opts[body] = list[i + 1];
                    i++;
                }
                else
                {
                    _opts[body] = "true";
                }
            }
            else if (a is "-h" or "-?")
            {
                _opts["help"] = "true";
            }
            else
            {
                Positional.Add(a);
            }
        }
    }

    public bool Has(string key) => _opts.ContainsKey(key);

    public string? Get(string key) => _opts.TryGetValue(key, out var v) ? v : null;

    public bool Flag(string key) => _opts.ContainsKey(key) && _opts[key] != "false";

    public int Int(string key, int fallback) => int.TryParse(Get(key), out var v) ? v : fallback;

    public string Str(string key, string fallback) => Get(key) ?? fallback;
}

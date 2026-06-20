using System.Text.Json;
using System.Text.Json.Serialization;

namespace TrackStash.Catalog.Output;

public static class CommandOutput
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static void WriteText(IEnumerable<(string Key, object? Value)> fields)
    {
        foreach (var (key, value) in fields)
            Console.WriteLine($"{key}: {value}");
    }

    public static void WriteJson(string command, bool ok, int exitCode, object? data, IEnumerable<string>? errors = null)
    {
        var envelope = new CommandEnvelope
        {
            Command = command,
            Ok = ok,
            ExitCode = exitCode,
            TimestampUtc = DateTimeOffset.UtcNow,
            Data = data,
            Errors = errors?.ToArray() ?? [],
        };

        Console.WriteLine(JsonSerializer.Serialize(envelope, JsonOptions));
    }

    public static bool IsJsonMode(string format)
        => string.Equals(format, "json", StringComparison.OrdinalIgnoreCase);
}

public sealed class CommandEnvelope
{
    public string Command { get; init; } = string.Empty;
    public bool Ok { get; init; }
    public int ExitCode { get; init; }
    public DateTimeOffset TimestampUtc { get; init; }
    public object? Data { get; init; }
    public string[] Errors { get; init; } = [];
}

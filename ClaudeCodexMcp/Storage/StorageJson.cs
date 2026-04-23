using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace ClaudeCodexMcp.Storage;

internal static class StorageJson
{
    public static readonly JsonSerializerOptions Options = CreateOptions();

    private static readonly JsonSerializerOptions LineOptions = CreateOptions(writeIndented: false);

    public static async Task WriteAtomicallyAsync<T>(string path, T value, CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = Path.Combine(directory ?? ".", $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        await using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            await JsonSerializer.SerializeAsync(stream, value, Options, cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }

        File.Move(tempPath, path, overwrite: true);
    }

    public static async Task<T?> ReadAsync<T>(string path, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path))
        {
            return default;
        }

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return await JsonSerializer.DeserializeAsync<T>(stream, Options, cancellationToken);
    }

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static string SerializeLine<T>(T value) => JsonSerializer.Serialize(value, LineOptions);

    public static T? Deserialize<T>(string value) => JsonSerializer.Deserialize<T>(value, Options);

    private static JsonSerializerOptions CreateOptions(bool writeIndented = true)
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = writeIndented
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));
        return options;
    }
}

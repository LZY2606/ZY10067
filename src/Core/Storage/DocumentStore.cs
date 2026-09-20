using System.Text.Json;
using System.Text.Json.Serialization;

namespace MicroStack.Core.Storage;

/// <summary>Atomic JSON document persistence: tmp file + fsync-equivalent flush + rename.</summary>
public sealed class JsonDocStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<T> ReadAsync<T>(string path, CancellationToken ct = default) where T : new()
    {
        if (!File.Exists(path)) return new T();
        await using var fs = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<T>(fs, Options, ct) ?? new T();
    }

    public async Task WriteAtomicAsync<T>(string path, T doc, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string tmp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        await using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true))
        {
            await JsonSerializer.SerializeAsync(fs, doc, Options, ct);
            await fs.FlushAsync(ct);
        }
        File.Move(tmp, path, overwrite: true);
    }
}

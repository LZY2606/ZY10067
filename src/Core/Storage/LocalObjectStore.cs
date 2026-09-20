using System.Security.Cryptography;

namespace MicroStack.Core.Storage;

/// <summary>
/// Local file-system object store under {root}/objects/ab/cd/{hash}{ext}.
/// Files are written atomically (tmp + rename) and never modified after creation.
/// </summary>
public sealed class LocalObjectStore : IObjectStore
{
    private readonly string _root;

    public LocalObjectStore(string root)
    {
        _root = Path.GetFullPath(root);
        Directory.CreateDirectory(_root);
    }

    public string RootPath => _root;

    public string PathFor(string key)
    {
        if (key.Contains("..", StringComparison.Ordinal) || key.Contains(Path.DirectorySeparatorChar))
            throw new ArgumentException("bad object key");
        string ext = "";
        int dot = key.LastIndexOf('.');
        string hash = dot < 0 ? key : key[..dot];
        if (dot >= 0) ext = key[dot..];
        if (hash.Length < 4) throw new ArgumentException("bad object key");
        return Path.Combine(_root, hash[..2], hash[2..4], key);
    }

    public async Task<string> PutAsync(byte[] data, string extension, CancellationToken ct = default)
    {
        string hash = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
        string key = hash + (extension.StartsWith('.') ? extension : "." + extension);
        string final = PathFor(key);
        if (File.Exists(final)) return key;
        Directory.CreateDirectory(Path.GetDirectoryName(final)!);
        string tmp = final + ".tmp-" + Guid.NewGuid().ToString("N");
        await File.WriteAllBytesAsync(tmp, data, ct);
        File.Move(tmp, final, overwrite: false);
        return key;
    }

    public async Task<Stream> OpenReadAsync(string key, CancellationToken ct = default) =>
        await Task.FromResult<Stream>(File.OpenRead(PathFor(key)));

    public async Task<byte[]> ReadAsync(string key, CancellationToken ct = default) =>
        await File.ReadAllBytesAsync(PathFor(key), ct);

    public bool Exists(string key) => File.Exists(PathFor(key));
}

using System.Security.Cryptography;

namespace MicroFocus.Core.Storage;

/// <summary>Versioned, regenerable artifacts under derivatives/&lt;compositeId&gt;/&lt;versionId&gt;/.</summary>
public sealed class DerivativeStore
{
    private readonly string _root;
    public DerivativeStore(string dataDir) => _root = Path.Combine(dataDir, "derivatives");

    public string RootPhysical => _root;

    public string VersionDir(string compositeId, string versionId) =>
        Path.Combine(_root, compositeId, versionId);

    public string WriteTile(string compositeId, string versionId, string fileName, byte[] bytes)
    {
        var dir = Path.Combine(_root, compositeId, versionId, "tiles");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, fileName);
        AtomicWrite(path, bytes);
        return path;
    }

    public string WriteVersionFile(string compositeId, string versionId, string fileName, byte[] bytes)
    {
        var dir = VersionDir(compositeId, versionId);
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, fileName);
        AtomicWrite(path, bytes);
        return path;
    }

    public static void AtomicWrite(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write))
        {
            fs.Write(bytes);
            fs.Flush(true);
        }
        File.Move(tmp, path, overwrite: true);
    }

    public void DeleteVersion(string compositeId, string versionId)
    {
        var dir = Path.Combine(_root, compositeId, versionId);
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }

    public static string Sha256(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}

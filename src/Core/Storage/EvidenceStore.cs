using System.Security.Cryptography;

namespace MicroFocus.Core.Storage;

/// <summary>Content-addressed, write-once raw evidence storage. Files are never modified in place.</summary>
public sealed class EvidenceStore
{
    private readonly string _root;
    public EvidenceStore(string dataDir) => _root = Path.Combine(dataDir, "evidence");

    public string Root => _root;

    public sealed class StoredEvidence
    {
        public required string Sha256 { get; init; }
        public required string RelativePath { get; init; }
        public required long SizeBytes { get; init; }
        public bool AlreadyExisted { get; init; }
    }

    public StoredEvidence Store(ReadOnlySpan<byte> bytes)
    {
        string sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        string dir = Path.Combine(_root, sha[..2]);
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, sha + ".bin");
        if (File.Exists(path))
        {
            var existing = File.ReadAllBytes(path);
            if (!existing.AsSpan().SequenceEqual(bytes))
                throw new InvalidOperationException($"证据内容寻址冲突：{sha} 已存在但字节不同（拒绝原地改写）");
            return new StoredEvidence { Sha256 = sha, RelativePath = Path.GetRelativePath(_root, path), SizeBytes = existing.Length, AlreadyExisted = true };
        }
        string tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write))
        {
            fs.Write(bytes);
            fs.Flush(true);
        }
        File.Move(tmp, path);
        return new StoredEvidence { Sha256 = sha, RelativePath = Path.GetRelativePath(_root, path), SizeBytes = bytes.Length };
    }

    public string ResolvePath(string sha256)
    {
        var path = Path.Combine(_root, sha256[..2], sha256 + ".bin");
        return File.Exists(path) ? path : throw new FileNotFoundException($"证据 {sha256} 缺失", path);
    }

    public byte[] Read(string sha256) => File.ReadAllBytes(ResolvePath(sha256));

    public bool Verify(string sha256)
    {
        string path = Path.Combine(_root, sha256[..2], sha256 + ".bin");
        if (!File.Exists(path)) return false;
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant() == sha256;
    }
}

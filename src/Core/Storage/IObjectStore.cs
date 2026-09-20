namespace MicroStack.Core.Storage;

/// <summary>Content-addressed immutable blob storage for raw evidence and derived artifacts.</summary>
public interface IObjectStore
{
    /// <summary>Stores bytes keyed by their SHA-256. Returns the hex content id (no prefix).</summary>
    Task<string> PutAsync(byte[] data, string extension, CancellationToken ct = default);

    /// <summary>Opens a read stream for a stored object, or throws if absent.</summary>
    Task<Stream> OpenReadAsync(string key, CancellationToken ct = default);

    Task<byte[]> ReadAsync(string key, CancellationToken ct = default);

    bool Exists(string key);

    string RootPath { get; }
}

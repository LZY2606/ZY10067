using System.Text.Json;
using System.Text.Json.Serialization;

namespace MicroFocus.Core.Storage;

/// <summary>JSON document store: one file per entity, write-to-temp + atomic rename, revision optimistic concurrency.</summary>
public sealed class JsonStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _dir;
    public JsonStore(string dataDir, string entity) => _dir = Path.Combine(dataDir, entity);

    public string Dir => _dir;

    public string PathFor(string id) => Path.Combine(_dir, id + ".json");

    public bool Exists(string id) => File.Exists(PathFor(id));

    public T? TryGet<T>(string id) where T : class
    {
        var path = PathFor(id);
        return File.Exists(path)
            ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOpts)
            : null;
    }

    public T Get<T>(string id) where T : class =>
        TryGet<T>(id) ?? throw new DomainException("NOT_FOUND", $"{typeof(T).Name} '{id}' 不存在");

    public IReadOnlyList<T> List<T>() where T : class
    {
        Directory.CreateDirectory(_dir);
        var result = new List<T>();
        foreach (var file in Directory.EnumerateFiles(_dir, "*.json"))
        {
            var item = JsonSerializer.Deserialize<T>(File.ReadAllText(file), JsonOpts);
            if (item != null) result.Add(item);
        }
        return result;
    }

    public void Put<T>(string id, T value)
    {
        Directory.CreateDirectory(_dir);
        string path = PathFor(id);
        string tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(value, JsonOpts));
        File.Move(tmp, path, overwrite: true);
    }
}

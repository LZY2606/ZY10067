using MicroStack.Core.Imaging;
using MicroStack.Core.Models;
using MicroStack.Core.Services;
using MicroStack.Core.Storage;

namespace MicroStack.Tests.Support;

/// <summary>Temp-dir backed harness with synthetic evidence + frame metadata helpers.</summary>
public sealed class TestHarness : IDisposable
{
    public AppService App { get; }
    public string Dir { get; }
    private int _counter;

    public TestHarness()
    {
        Dir = Path.Combine(Path.GetTempPath(), "microstack-test-" + Guid.NewGuid().ToString("N"));
        var store = new LocalObjectStore(Path.Combine(Dir, "objects"));
        App = new AppService(Dir, store);
    }

    public byte[] MakePng(int w, int h, Func<int, int, byte> fn)
    {
        var img = new PngImage(w, h, 1);
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
            img.SetGray(x, y, fn(x, y));
        return Png.Encode(img);
    }

    public async Task<string> IngestAsync(byte[] png, string? name = null)
    {
        _counter++;
        var r = await App.IngestEvidenceAsync(name ?? $"frame{_counter}.png", png);
        return r.EvidenceId;
    }

    public static FrameInput Frame(string evidenceId, double mag, double sx, double sy,
        string xUnit = "um", string yUnit = "um", double z = 0, string? at = null) => new()
    {
        EvidenceId = evidenceId,
        Magnification = mag,
        StageX = sx,
        StageY = sy,
        StageXUnit = xUnit,
        StageYUnit = yUnit,
        ZHeightUm = z,
        CapturedAt = at
    };

    public async Task<CompositionDoc> CreateStackAsync(
        int size = 96, int tileSize = 48, int minimumAligned = 1)
    {
        // Three same-field frames whose sharp content sits in distinct vertical bands.
        var ids = new List<string>
        {
            await IngestAsync(MakePng(size, size, (x, y) => BandValue(x, y, 0, size)), "a.png"),
            await IngestAsync(MakePng(size, size, (x, y) => BandValue(x, y, 1, size)), "b.png"),
            await IngestAsync(MakePng(size, size, (x, y) => BandValue(x, y, 2, size)), "c.png"),
        };
        var frames = ids.Select((id, i) => Frame(id, 10, 0, 0, z: i * 3,
            at: $"2026-01-01T00:00:0{i}Z")).ToList();
        return await App.CreateCompositionAsync(new CreateCompositionRequest
        {
            Name = "stack",
            Frames = frames,
            Parameters = new EngineParameters
            {
                TileSize = tileSize,
                MinimumAlignedFrames = minimumAligned,
                SharpnessWindowRadius = 2,
                EdgeGuardPixels = 2
            }
        });
    }

    public static byte BandValue(int x, int y, int band, int size)
    {
        int b = y * 3 / size;
        bool sharp = b == band;
        double checker = ((x / 3) + (y / 3)) % 2 == 0 ? 1.0 : 0.0;
        double v = sharp ? 30 + checker * 220 : 110 + checker * 10;
        return (byte)v;
    }

    public void Dispose()
    {
        try { if (Directory.Exists(Dir)) Directory.Delete(Dir, recursive: true); } catch { }
    }
}

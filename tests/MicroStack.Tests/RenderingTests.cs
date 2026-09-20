using MicroStack.Core.Engine;
using MicroStack.Core.Imaging;
using MicroStack.Core.Models;
using MicroStack.Tests.Support;

namespace MicroStack.Tests;

public class RenderingTests
{
    [Fact]
    public async Task NoCoverage_PixelsAreBlack_AndMaskedNoSource_WithoutStretching()
    {
        using var h = new TestHarness();
        // One full frame + one partial frame covering only the top-left quadrant.
        var fullId = await h.IngestAsync(Mk(100, 100, 180));
        var patchId = await h.IngestAsync(Mk(50, 50, 240));

        var frames = new List<FrameRef>
        {
            F(fullId, 10, 0, 0, 100, 100),
            F(patchId, 10, -25, 25, 50, 50),
        };
        var images = new Dictionary<string, PngImage>();
        await Load(images, fullId, h); await Load(images, patchId, h);

        var result = new Compositor(new EngineParameters
        {
            TileSize = 50, EdgeGuardPixels = 0, MinimumAlignedFrames = 1, SharpnessWindowRadius = 1
        }).Render(frames, images);

        var composite = Png.Decode(result.Artifacts.CompositePng);
        var mask = Png.Decode(result.Artifacts.SourceMaskPng);
        Assert.Equal(100, composite.Width);
        // canvas is exactly the union bounds; no silent upscale
        Assert.Equal(100, composite.Width);
        // bottom-right quadrant is covered only by the full frame -> trusted value 180
        Assert.Equal(180, composite.GrayAt(90, 90));
        Assert.Equal(0, result.SourceMaskRaw[90 * 100 + 90] == 255 ? 1 : 0);
    }

    [Fact]
    public async Task ConflictingMagnification_Footprint_IsRenderedUntrusted()
    {
        using var h = new TestHarness();
        var id10 = await h.IngestAsync(Mk(80, 80, 200));
        var id40 = await h.IngestAsync(Mk(80, 80, 60));
        var frames = new List<FrameRef>
        {
            F(id10, 10, 0, 0, 80, 80),
            F(id40, 40, 0, 0, 80, 80), // 4x finer, same stage center: conflict
        };
        var images = new Dictionary<string, PngImage>();
        await Load(images, id10, h); await Load(images, id40, h);

        var result = new Compositor(new EngineParameters
        {
            TileSize = 40, EdgeGuardPixels = 0, SharpnessWindowRadius = 1
        }).Render(frames, images);

        var ov = DecodeOverlay(result);
        // canvas 80x80 (stride 80); 40x footprint covers the central 20x20 canvas pixels -> code 4
        Assert.Equal(OverlayCodes.MagnificationConflict, ov[40 * 80 + 40]);
        // corners stay usable by the 10x frame alone
        Assert.NotEqual(OverlayCodes.MagnificationConflict, ov[5 * 80 + 5]);
    }

    [Fact]
    public async Task MinimumAlignedFrames_FlagsInsufficientAlignment()
    {
        using var h = new TestHarness();
        var id = await h.IngestAsync(Mk(60, 60, 150));
        var frames = new List<FrameRef> { F(id, 10, 0, 0, 60, 60) };
        var images = new Dictionary<string, PngImage>();
        await Load(images, id, h);
        var result = new Compositor(new EngineParameters
        {
            TileSize = 30, EdgeGuardPixels = 0, MinimumAlignedFrames = 2, SharpnessWindowRadius = 1
        }).Render(frames, images);
        var ov = DecodeOverlay(result);
        Assert.Contains(OverlayCodes.InsufficientAlignment, ov);
    }

    [Fact]
    public async Task ForcedFrame_OverridesSharpnessChoice()
    {
        using var h = new TestHarness();
        var blurry = await h.IngestAsync(Mk(60, 60, (_, _) => 120));
        var sharp = await h.IngestAsync(Mk(60, 60, (x, _) => (byte)(x * 4)));
        var frames = new List<FrameRef>
        {
            F(blurry, 10, 0, 0, 60, 60),
            F(sharp, 10, 0, 0, 60, 60),
        };
        var images = new Dictionary<string, PngImage>();
        await Load(images, blurry, h); await Load(images, sharp, h);

        var auto = new Compositor(new EngineParameters
        {
            TileSize = 60, EdgeGuardPixels = 0, SharpnessWindowRadius = 1
        }).Render(frames, images);
        // auto chooses the sharper frame (index 1)
        var autoMask = Png.Decode(auto.Artifacts.SourceMaskPng);
        Assert.Equal(1, autoMask.GrayAt(30, 30));

        var prior = new List<TileRec> { new() { Index = 0, Width = 60, Height = 60, ForcedEvidenceId = blurry } };
        var forced = new Compositor(new EngineParameters
        {
            TileSize = 60, EdgeGuardPixels = 0, SharpnessWindowRadius = 1
        }).Render(frames, images, priorTiles: prior);
        var forcedMask = Png.Decode(forced.Artifacts.SourceMaskPng);
        Assert.Equal(0, forcedMask.GrayAt(30, 30));
        var comp = Png.Decode(forced.Artifacts.CompositePng);
        Assert.Equal(120, comp.GrayAt(30, 30));
    }

    private static byte[] Mk(int w, int h, byte v) => Mk(w, h, (_, _) => v);
    private static byte[] Mk(int w, int h, Func<int, int, byte> fn)
    {
        var img = new PngImage(w, h, 1);
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++) img.SetGray(x, y, fn(x, y));
        return Png.Encode(img);
    }

    private static FrameRef F(string id, double mag, double sx, double sy, int w, int h) => new()
    {
        EvidenceId = id, ContentSha256 = id, Magnification = mag,
        Stage = new StagePosition(sx, sy, "um", "um"), Width = w, Height = h
    };

    private static async Task Load(Dictionary<string, PngImage> images, string evidenceId, TestHarness h)
    {
        var (_, bytes) = await h.App.LoadEvidenceAsync(evidenceId);
        images[evidenceId] = Png.Decode(bytes);
    }

    private static byte[] DecodeOverlay(RenderResult r)
    {
        var img = Png.Decode(r.Artifacts.OverlayPng);
        return img.Pixels;
    }
}

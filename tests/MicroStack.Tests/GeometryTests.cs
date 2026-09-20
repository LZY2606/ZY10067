using MicroStack.Core.Engine;
using MicroStack.Core.Models;
using MicroStack.Tests.Support;

namespace MicroStack.Tests;

public class GeometryTests
{
    private static FrameRef Frame(string id, double? mag, double? sx, double? sy,
        string? xu = "um", string? yu = "um", int w = 100, int h = 80) => new()
    {
        EvidenceId = id,
        ContentSha256 = id,
        Magnification = mag ?? 0,
        MagnificationMissing = mag == null,
        Stage = new StagePosition(sx, sy, xu, yu),
        Width = w,
        Height = h
    };

    [Fact]
    public void MissingUnits_IsNotPlaced_AndFlagged()
    {
        var frames = new List<FrameRef>
        {
            Frame("a", 10, 0, 0),
            Frame("b", 10, 5, 5, null, null),
        };
        var r = Geometry.BuildLayout(frames, new EngineParameters());
        var b = r.Layouts.Single(l => l.EvidenceId == "b");
        Assert.False(b.Placed);
        Assert.Equal("UnitsMissing", b.Reason);
        Assert.Contains(r.Diagnostics, d => d.EvidenceId == "b" && d.Code == "UnitsMissing");
    }

    [Fact]
    public void MissingMagnification_IsFlagged_CalibrationMissing()
    {
        var frames = new List<FrameRef> { Frame("a", null, 0, 0) };
        var r = Geometry.BuildLayout(frames, new EngineParameters());
        Assert.False(r.Layouts[0].Placed);
        Assert.Equal("CalibrationMissing", r.Layouts[0].Reason);
    }

    [Fact]
    public void MagnificationConflict_IsPlacedButMarked()
    {
        var frames = new List<FrameRef>
        {
            Frame("a", 10, 0, 0),
            Frame("b", 10, 10, 10),
            Frame("c", 20, 0, 0),
        };
        var r = Geometry.BuildLayout(frames, new EngineParameters());
        var c = r.Layouts.Single(l => l.EvidenceId == "c");
        Assert.True(c.Placed);
        Assert.True(c.MagnificationConflict);
        Assert.Equal(10, r.ReferenceMagnification);
    }

    [Fact]
    public void PartialFrame_DrawsAtItsStageFootprint_WithoutStretchingCanvas()
    {
        var frames = new List<FrameRef>
        {
            Frame("full", 10, 0, 0, w: 100, h: 100),
            Frame("patch", 10, -25, 25, w: 50, h: 50),
        };
        var r = Geometry.BuildLayout(frames, new EngineParameters { PixelScaleFactorUm = 10 });
        // full = 1um/px, spans -50..50um; patch center (-25,25), spans -50..0 / 0..50
        var patch = r.Layouts.Single(l => l.EvidenceId == "patch");
        Assert.Equal(0, patch.DrawX0);       // -50um after normalisation
        Assert.Equal(0, patch.DrawY0);
        Assert.Equal(50, patch.DrawX1);
        Assert.Equal(50, patch.DrawY1);
        Assert.Equal(100, r.CanvasWidth);
        Assert.Equal(100, r.CanvasHeight);
    }
}

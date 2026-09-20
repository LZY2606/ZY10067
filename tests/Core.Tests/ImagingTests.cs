using MicroFocus.Core.Imaging;
using Xunit;

namespace MicroFocus.Core.Tests;

public class ImagingTests
{
    [Fact]
    public void PnmCodec_RoundTrips_P5()
    {
        var bytes = TestData.Pgm(8, 5, (x, y) => (byte)(x * 17 + y * 7));
        var img = PnmCodec.Decode(bytes);
        Assert.Equal(8, img.Width);
        Assert.Equal(5, img.Height);
        Assert.Equal(1, img.Channels);
        Assert.Equal(17, img.Gray[1]);
        var again = PnmCodec.EncodeP5(img.Gray, img.Width, img.Height);
        Assert.Equal(bytes, again);
    }

    [Fact]
    public void PnmCodec_Rejects_TruncatedRaster()
    {
        var bad = "P5\n4 4\n255\n"u8.ToArray().Concat(new byte[] { 1, 2, 3 }).ToArray();
        Assert.Throws<InvalidDataException>(() => PnmCodec.Decode(bad));
    }

    [Fact]
    public void Affine_Inverse_RoundTrips()
    {
        var fwd = Affine.FrameToCanvas(0.32, 100, 200, 0.16, 50, 60);
        var inv = fwd.Inverse();
        var (x, y) = inv.Apply(fwd.Apply(12.7, -8.3).x, fwd.Apply(12.7, -8.3).y);
        Assert.Equal(12.7, x, 9);
        Assert.Equal(-8.3, y, 9);
    }

    [Fact]
    public void Sharpness_HigherOnHighFrequencyPatch()
    {
        var flat = TestData.FlatFrame(64, 64, 90);
        var sharp = TestData.DetailFrame(64, 64, 20, 20, 24);
        var flatImg = PnmCodec.Decode(flat);
        var sharpImg = PnmCodec.Decode(sharp);
        double avg(byte[] g) => MicroFocus.Core.Services.Compositor.BuildSharpness(g, 64, 64).Average();
        Assert.True(avg(sharpImg.Gray) > avg(flatImg.Gray) * 20);
    }
}

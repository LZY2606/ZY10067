using MicroStack.Core.Imaging;

namespace MicroStack.Tests;

public class PngCodecTests
{
    [Fact]
    public void Grayscale_RoundTrip_PreservesEveryPixel()
    {
        var img = new PngImage(47, 31, 1);
        var rng = new Random(7);
        rng.NextBytes(img.Pixels);

        var decoded = Png.Decode(Png.Encode(img));

        Assert.Equal(47, decoded.Width);
        Assert.Equal(31, decoded.Height);
        Assert.Equal(1, decoded.Channels);
        Assert.Equal(img.Pixels, decoded.Pixels);
    }

    [Fact]
    public void Rgb_RoundTrip_PreservesPixels()
    {
        var img = new PngImage(20, 12, 3);
        for (int y = 0; y < 12; y++)
        for (int x = 0; x < 20; x++)
        {
            int i = (y * 20 + x) * 3;
            img.Pixels[i] = (byte)(x * 12);
            img.Pixels[i + 1] = (byte)(y * 20);
            img.Pixels[i + 2] = (byte)((x + y) * 8);
        }
        var decoded = Png.Decode(Png.Encode(img));
        Assert.Equal(3, decoded.Channels);
        Assert.Equal(img.Pixels, decoded.Pixels);
    }

    [Theory]
    [InlineData(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 })]
    public void Rejects_BadSignature(byte[] data)
    {
        Assert.Throws<InvalidDataException>(() => Png.Decode(data));
    }
}

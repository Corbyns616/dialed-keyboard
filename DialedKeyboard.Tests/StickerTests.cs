using DialedKeyboard.Stickers;

namespace DialedKeyboard.Tests;

public class StickerTests
{
    /// <summary>A 100×100 red photo whose mask marks a 40×30 block (and optionally a one-pixel speck) as the subject.</summary>
    static (Picture Photo, float[] Mask) Photo(bool speck)
    {
        const int w = 100, h = 100;
        var pixels = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++)
        {
            pixels[i * 4 + 2] = 255; // red
            pixels[i * 4 + 3] = 255;
        }
        var mask = new float[w * h];
        for (int y = 20; y < 50; y++)
            for (int x = 30; x < 70; x++)
                mask[y * w + x] = 1;
        if (speck) mask[90 * w + 5] = 1;
        return (new Picture(w, h, pixels), mask);
    }

    static (byte B, byte G, byte R, byte A) Pixel(Picture p, int x, int y)
    {
        int i = (y * p.Width + x) * 4;
        return (p.Pixels[i], p.Pixels[i + 1], p.Pixels[i + 2], p.Pixels[i + 3]);
    }

    [Fact]
    public void Cut_CropsToSubjectPlusOutline_AndDropsSpecks()
    {
        var (photo, mask) = Photo(speck: true);
        var sticker = StickerMaker.Cut(photo, mask, outline: 4)!;
        // 40×30 subject, plus outline and 2px of breathing room on every side; the speck isn't included.
        Assert.Equal(40 + 2 * 6, sticker.Width);
        Assert.Equal(30 + 2 * 6, sticker.Height);
    }

    [Fact]
    public void Cut_KeepsSubjectColours_OutlinesInWhite_AndClearsTheRest()
    {
        var (photo, mask) = Photo(speck: false);
        var sticker = StickerMaker.Cut(photo, mask, outline: 4)!;
        Assert.Equal((0, 0, 255, 255), Pixel(sticker, sticker.Width / 2, sticker.Height / 2)); // subject: red
        Assert.Equal((255, 255, 255, 255), Pixel(sticker, 3, sticker.Height / 2));            // outline: white
        Assert.Equal(0, Pixel(sticker, 0, 0).A);                                              // corner: clear
    }

    [Fact]
    public void Cut_ReturnsNull_WhenThereIsNoSubject()
    {
        var (photo, _) = Photo(speck: false);
        Assert.Null(StickerMaker.Cut(photo, new float[100 * 100], outline: 4));
    }

    [Fact]
    public void WikimediaPictures_UseWikimediasOwnPreviews()
    {
        Assert.Equal("https://upload.wikimedia.org/wikipedia/commons/thumb/f/f1/Dog_%28pet%29.jpg/330px-Dog_%28pet%29.jpg",
            StickerSearch.WikimediaThumbnail("https://upload.wikimedia.org/wikipedia/commons/f/f1/Dog_%28pet%29.jpg", 330));
        Assert.EndsWith("/960px-Logo.svg.png",
            StickerSearch.WikimediaThumbnail("https://upload.wikimedia.org/wikipedia/commons/a/ab/Logo.svg", 960));
        Assert.Null(StickerSearch.WikimediaThumbnail("https://live.staticflickr.com/65535/1_b.jpg", 330));
    }

    [Fact]
    public void ResizeMask_StretchesSmoothly()
    {
        var mask = StickerMaker.ResizeMask(new[] { 0f, 1f }, 2, 1, 4, 1);
        Assert.Equal(new[] { 0f, 0.25f, 0.75f, 1f }, mask);
    }
}

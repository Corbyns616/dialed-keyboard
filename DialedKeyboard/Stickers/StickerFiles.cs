using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DialedKeyboard.Stickers;

/// <summary>Saved stickers (PNG files in the data folder) and converting pictures to and from WPF images.</summary>
public static class StickerFiles
{
    public static string Folder => Path.Combine(AppSettings.DataDir, "Stickers");

    /// <summary>Saved stickers, newest first.</summary>
    public static List<string> All()
    {
        if (!Directory.Exists(Folder)) return new List<string>();
        return Directory.EnumerateFiles(Folder, "*.png")
            .OrderByDescending(Path.GetFileName, StringComparer.Ordinal)
            .ToList();
    }

    public static string Save(byte[] png)
    {
        Directory.CreateDirectory(Folder);
        string path = Path.Combine(Folder, DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + ".png");
        File.WriteAllBytes(path, png);
        return path;
    }

    public static void Delete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>Decodes a picture, shrunk so its longer side is at most <paramref name="maxSide"/>.</summary>
    public static Picture Decode(byte[] data, int maxSide)
    {
        var frame = BitmapDecoder.Create(new MemoryStream(data), BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
        BitmapSource src = frame;
        double scale = Math.Min(1, maxSide / (double)Math.Max(frame.PixelWidth, frame.PixelHeight));
        if (scale < 1) src = new TransformedBitmap(src, new ScaleTransform(scale, scale));
        src = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
        int w = src.PixelWidth, h = src.PixelHeight;
        var pixels = new byte[w * h * 4];
        src.CopyPixels(pixels, w * 4, 0);
        return new Picture(w, h, pixels);
    }

    public static BitmapSource ToBitmap(Picture p)
    {
        var bmp = BitmapSource.Create(p.Width, p.Height, 96, 96, PixelFormats.Bgra32, null, p.Pixels, p.Width * 4);
        bmp.Freeze();
        return bmp;
    }

    public static byte[] ToPng(Picture p)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(ToBitmap(p)));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    /// <summary>A small, frozen image for a button, from file bytes.</summary>
    public static BitmapImage Thumbnail(byte[] data, int width)
    {
        var img = new BitmapImage();
        img.BeginInit();
        img.CacheOption = BitmapCacheOption.OnLoad;
        img.DecodePixelWidth = width;
        img.StreamSource = new MemoryStream(data);
        img.EndInit();
        img.Freeze();
        return img;
    }

    /// <summary>The sticker over white, for apps that can only paste pictures without transparency.</summary>
    public static BitmapSource OnWhite(BitmapSource sticker)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var rect = new System.Windows.Rect(0, 0, sticker.PixelWidth, sticker.PixelHeight);
            dc.DrawRectangle(Brushes.White, null, rect);
            dc.DrawImage(sticker, rect);
        }
        var bmp = new RenderTargetBitmap(sticker.PixelWidth, sticker.PixelHeight, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(visual);
        bmp.Freeze();
        return bmp;
    }
}

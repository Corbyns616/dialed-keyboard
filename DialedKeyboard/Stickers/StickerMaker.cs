namespace DialedKeyboard.Stickers;

/// <summary>A picture as 32-bit BGRA pixels with straight (not premultiplied) alpha, row by row.</summary>
public sealed record Picture(int Width, int Height, byte[] Pixels);

/// <summary>
/// Turns a photo plus a mask of its subject into a sticker, like a phone's "lift subject":
/// the background goes, stray specks are dropped, and the subject gets a white outline and is
/// cropped close.
/// </summary>
public static class StickerMaker
{
    /// <summary>Mask values at or above this count as the subject.</summary>
    const float Solid = 0.5f;

    /// <summary>
    /// Cuts the subject out of <paramref name="photo"/>. <paramref name="mask"/> holds 0 to 1 per
    /// pixel (1 = subject). Returns null if there's no clear subject.
    /// </summary>
    public static Picture? Cut(Picture photo, float[] mask, int outline)
    {
        int w = photo.Width, h = photo.Height;
        bool[] keep = MainSubject(mask, w, h);

        int x0 = w, y0 = h, x1 = -1, y1 = -1;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                if (keep[y * w + x])
                {
                    x0 = Math.Min(x0, x); x1 = Math.Max(x1, x);
                    y0 = Math.Min(y0, y); y1 = Math.Max(y1, y);
                }
        if (x1 < 0) return null;

        int pad = outline + 2;
        int left = x0 - pad, top = y0 - pad;
        int cw = x1 - x0 + 1 + 2 * pad, ch = y1 - y0 + 1 + 2 * pad;
        float[] dist = DistanceFrom((x, y) =>
        {
            int sx = x + left, sy = y + top;
            return sx >= 0 && sy >= 0 && sx < w && sy < h && keep[sy * w + sx];
        }, cw, ch);

        var src = photo.Pixels;
        var dst = new byte[cw * ch * 4];
        for (int y = 0; y < ch; y++)
            for (int x = 0; x < cw; x++)
            {
                int sx = x + left, sy = y + top;
                float d = dist[y * cw + x];
                bool inPhoto = sx >= 0 && sy >= 0 && sx < w && sy < h;
                // The soft edge of the subject is kept; soft haze away from it (specks we dropped) isn't.
                float a = inPhoto && d <= 1.5f ? mask[sy * w + sx] : 0;
                if (a < 0.04f) a = 0;
                float o = outline > 0 ? Math.Clamp(outline + 0.5f - d, 0, 1) : 0;
                float outA = a + (1 - a) * o;
                if (outA <= 0) continue;

                int di = (y * cw + x) * 4;
                int si = inPhoto ? (sy * w + sx) * 4 : 0;
                for (int c = 0; c < 3; c++)
                {
                    float sc = inPhoto ? src[si + c] : 0;
                    dst[di + c] = (byte)Math.Clamp((sc * a + 255 * o * (1 - a)) / outA + 0.5f, 0, 255);
                }
                dst[di + 3] = (byte)Math.Clamp(outA * 255 + 0.5f, 0, 255);
            }
        return new Picture(cw, ch, dst);
    }

    /// <summary>
    /// The solid parts of the mask that belong to the subject: the biggest blob, plus any other
    /// blob at least a tenth its size (a second dog, a detached tail). Specks are dropped.
    /// </summary>
    static bool[] MainSubject(float[] mask, int w, int h)
    {
        var label = new int[w * h];
        var sizes = new List<int> { 0 }; // label 0 = not subject
        var stack = new Stack<int>();
        for (int start = 0; start < w * h; start++)
        {
            if (label[start] != 0 || mask[start] < Solid) continue;
            int id = sizes.Count, size = 0;
            label[start] = id;
            stack.Push(start);
            while (stack.Count > 0)
            {
                int i = stack.Pop();
                size++;
                int x = i % w, y = i / w;
                if (x > 0) Visit(i - 1);
                if (x < w - 1) Visit(i + 1);
                if (y > 0) Visit(i - w);
                if (y < h - 1) Visit(i + w);
            }
            sizes.Add(size);

            void Visit(int n)
            {
                if (label[n] != 0 || mask[n] < Solid) return;
                label[n] = id;
                stack.Push(n);
            }
        }

        int biggest = sizes.Max();
        // Less than 0.3% of the picture isn't a subject, it's noise.
        if (biggest < w * h * 0.003) return new bool[w * h];
        var keep = new bool[w * h];
        for (int i = 0; i < keep.Length; i++)
            keep[i] = label[i] != 0 && sizes[label[i]] * 10 >= biggest;
        return keep;
    }

    /// <summary>Roughly how far each pixel is from the nearest subject pixel (0 inside it).</summary>
    static float[] DistanceFrom(Func<int, int, bool> inside, int w, int h)
    {
        const float Far = 1e6f, Diag = 1.4142135f;
        var d = new float[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                d[y * w + x] = inside(x, y) ? 0 : Far;

        // Two-pass chamfer distance: down-right, then up-left.
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                float v = d[i];
                if (x > 0) v = Math.Min(v, d[i - 1] + 1);
                if (y > 0)
                {
                    v = Math.Min(v, d[i - w] + 1);
                    if (x > 0) v = Math.Min(v, d[i - w - 1] + Diag);
                    if (x < w - 1) v = Math.Min(v, d[i - w + 1] + Diag);
                }
                d[i] = v;
            }
        for (int y = h - 1; y >= 0; y--)
            for (int x = w - 1; x >= 0; x--)
            {
                int i = y * w + x;
                float v = d[i];
                if (x < w - 1) v = Math.Min(v, d[i + 1] + 1);
                if (y < h - 1)
                {
                    v = Math.Min(v, d[i + w] + 1);
                    if (x < w - 1) v = Math.Min(v, d[i + w + 1] + Diag);
                    if (x > 0) v = Math.Min(v, d[i + w - 1] + Diag);
                }
                d[i] = v;
            }
        return d;
    }

    /// <summary>Bilinear resize of a one-channel mask.</summary>
    public static float[] ResizeMask(float[] mask, int mw, int mh, int w, int h)
    {
        var result = new float[w * h];
        for (int y = 0; y < h; y++)
        {
            float fy = Math.Clamp((y + 0.5f) * mh / h - 0.5f, 0, mh - 1);
            int y0 = (int)fy, y1 = Math.Min(y0 + 1, mh - 1);
            float ty = fy - y0;
            for (int x = 0; x < w; x++)
            {
                float fx = Math.Clamp((x + 0.5f) * mw / w - 0.5f, 0, mw - 1);
                int x0 = (int)fx, x1 = Math.Min(x0 + 1, mw - 1);
                float tx = fx - x0;
                float top = mask[y0 * mw + x0] * (1 - tx) + mask[y0 * mw + x1] * tx;
                float bottom = mask[y1 * mw + x0] * (1 - tx) + mask[y1 * mw + x1] * tx;
                result[y * w + x] = top * (1 - ty) + bottom * ty;
            }
        }
        return result;
    }

    /// <summary>Bilinear sample of one colour channel (0 = blue, 1 = green, 2 = red) at a pixel position.</summary>
    public static float Sample(Picture p, float fx, float fy, int channel)
    {
        fx = Math.Clamp(fx, 0, p.Width - 1);
        fy = Math.Clamp(fy, 0, p.Height - 1);
        int x0 = (int)fx, y0 = (int)fy;
        int x1 = Math.Min(x0 + 1, p.Width - 1), y1 = Math.Min(y0 + 1, p.Height - 1);
        float tx = fx - x0, ty = fy - y0;
        float At(int x, int y) => p.Pixels[(y * p.Width + x) * 4 + channel];
        return (At(x0, y0) * (1 - tx) + At(x1, y0) * tx) * (1 - ty) + (At(x0, y1) * (1 - tx) + At(x1, y1) * tx) * ty;
    }
}

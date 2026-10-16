using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace DialedKeyboard.Stickers;

/// <summary>
/// Finds the subject of a photo with U²-Net (the small "u2netp" model), on this computer.
/// Photos are never uploaded anywhere to remove their background.
/// </summary>
public sealed class BackgroundRemover : IDisposable
{
    const int Size = 320;
    static readonly float[] Mean = { 0.485f, 0.456f, 0.406f };
    static readonly float[] Std = { 0.229f, 0.224f, 0.225f };

    readonly InferenceSession _session;

    public BackgroundRemover(string modelPath) => _session = new InferenceSession(modelPath);

    public static string DefaultModelPath => Path.Combine(AppContext.BaseDirectory, "Models", "u2netp.onnx");

    /// <summary>How much each pixel of <paramref name="photo"/> belongs to the subject, 0 to 1.</summary>
    public float[] Mask(Picture photo)
    {
        // The model takes a 320×320 RGB image, scaled so its brightest value is 1, then normalised
        // the ImageNet way.
        var rgb = new float[3, Size, Size];
        float max = 1e-6f;
        for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
            {
                float fx = (x + 0.5f) * photo.Width / Size - 0.5f, fy = (y + 0.5f) * photo.Height / Size - 0.5f;
                for (int c = 0; c < 3; c++)
                {
                    float v = StickerMaker.Sample(photo, fx, fy, 2 - c) / 255f; // BGRA → RGB
                    rgb[c, y, x] = v;
                    max = Math.Max(max, v);
                }
            }
        var input = new DenseTensor<float>(new[] { 1, 3, Size, Size });
        for (int c = 0; c < 3; c++)
            for (int y = 0; y < Size; y++)
                for (int x = 0; x < Size; x++)
                    input[0, c, y, x] = (rgb[c, y, x] / max - Mean[c]) / Std[c];

        string name = _session.InputMetadata.Keys.First();
        using var results = _session.Run(new[] { NamedOnnxValue.CreateFromTensor(name, input) });
        var output = results.First().AsTensor<float>();

        var mask = new float[Size * Size];
        float lo = float.MaxValue, hi = float.MinValue;
        for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
            {
                float v = output[0, 0, y, x];
                mask[y * Size + x] = v;
                lo = Math.Min(lo, v);
                hi = Math.Max(hi, v);
            }
        float range = Math.Max(hi - lo, 1e-6f);
        for (int i = 0; i < mask.Length; i++) mask[i] = (mask[i] - lo) / range;
        return StickerMaker.ResizeMask(mask, Size, Size, photo.Width, photo.Height);
    }

    public void Dispose() => _session.Dispose();
}

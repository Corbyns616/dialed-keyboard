using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace DialedKeyboard;

/// <summary>
/// Real mechanical-keyboard clicks on every key press, from the NK Cream pack
/// (Mechvibes, MIT; the same sounds as ASMR Typer). One WAV per key in Sounds\&lt;pack&gt;\:
/// "a.wav" plays for A, "space.wav", "enter.wav", "backspace.wav", "shift.wav", "tab.wav"
/// for those keys, and keys without their own file pick a random letter. Clips are mixed
/// into one always-open output stream, so fast taps overlap instead of cutting each other off.
/// </summary>
public sealed class KeySounds : IDisposable
{
    const int Rate = 44100;
    static readonly string[] Modifiers = { "shift", "tab", "caps lock", "backspace", "space", "enter" };

    readonly Dictionary<string, float[]> _byKey = new();
    readonly List<float[]> _regular = new();
    readonly Random _rng = new();
    MixingSampleProvider? _mixer;
    WaveOutEvent? _output;

    public bool Enabled { get; set; } = true;
    /// <summary>0 to 1.</summary>
    public float Volume { get; set; } = 0.7f;

    public KeySounds(string pack = "nk-cream")
    {
        string dir = Path.Combine(AppContext.BaseDirectory, "Sounds", pack);
        if (!Directory.Exists(dir)) return;
        foreach (var file in Directory.GetFiles(dir, "*.wav"))
        {
            var clip = Load(file);
            if (clip == null) continue;
            string key = Path.GetFileNameWithoutExtension(file).ToLowerInvariant();
            _byKey[key] = clip;
            if (!Modifiers.Contains(key)) _regular.Add(clip);
        }
        if (_regular.Count == 0) return;

        try
        {
            _mixer = new MixingSampleProvider(WaveFormat.CreateIeeeFloatWaveFormat(Rate, 2)) { ReadFully = true };
            _output = new WaveOutEvent { DesiredLatency = 60, NumberOfBuffers = 3 };
            _output.Init(_mixer);
            _output.Play();
        }
        catch (Exception)
        {
            // No audio device: the keyboard still types, just silently.
            _output?.Dispose();
            _output = null;
            _mixer = null;
        }
    }

    /// <summary>Plays the click for a key: a letter ('a'), or a named key ("space", "enter", "backspace", "shift", "tab").</summary>
    public void Play(string key)
    {
        if (!Enabled || _mixer == null || Volume <= 0) return;
        var clip = _byKey.TryGetValue(key.ToLowerInvariant(), out var own) ? own : _regular[_rng.Next(_regular.Count)];
        // Small level changes so repeated keys don't sound copy-pasted.
        float gain = Volume * (float)(0.82 + 0.18 * _rng.NextDouble());
        _mixer.AddMixerInput(new Clip(clip, gain));
    }

    static float[]? Load(string path)
    {
        try
        {
            using var reader = new WaveFileReader(path);
            ISampleProvider src = reader.ToSampleProvider();
            if (src.WaveFormat.Channels == 1) src = new MonoToStereoSampleProvider(src);
            if (src.WaveFormat.SampleRate != Rate) return null;
            var all = new List<float>();
            var buf = new float[4096];
            int n;
            while ((n = src.Read(buf, 0, buf.Length)) > 0) all.AddRange(buf.AsSpan(0, n).ToArray());

            // Trim the silence before the click so it lands the moment the key is pressed.
            float peak = all.Count == 0 ? 0 : all.Max(Math.Abs);
            int start = all.FindIndex(v => Math.Abs(v) >= peak * 0.03f);
            start = Math.Max(0, start - 44) & ~1;
            return all.Skip(start).ToArray();
        }
        catch (Exception)
        {
            return null;
        }
    }

    sealed class Clip : ISampleProvider
    {
        readonly float[] _data;
        readonly float _gain;
        int _pos;

        public Clip(float[] data, float gain) { _data = data; _gain = gain; }

        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(Rate, 2);

        public int Read(float[] buffer, int offset, int count)
        {
            int n = Math.Min(count, _data.Length - _pos);
            for (int i = 0; i < n; i++) buffer[offset + i] = _data[_pos + i] * _gain;
            _pos += n;
            return n;
        }
    }

    public void Dispose()
    {
        _output?.Stop();
        _output?.Dispose();
    }
}

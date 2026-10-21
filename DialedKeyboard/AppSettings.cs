using System.Text.Json;

namespace DialedKeyboard;

public sealed class ExpansionItem
{
    public string Abbreviation { get; set; } = "";
    public string Expansion { get; set; } = "";
}

public sealed class AppSettings
{
    public static string DataDir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DialedKeyboard");
    public static string SettingsPath => Path.Combine(DataDir, "settings.json");
    public static string UserDictionaryPath => Path.Combine(DataDir, "userdict.json");

    public double Opacity { get; set; } = 0.97;
    public string Theme { get; set; } = "Dark";
    public double? Left { get; set; }
    public double? Top { get; set; }
    public double Width { get; set; } = 600;
    public double Height { get; set; } = 456;
    public double? MiniLeft { get; set; }
    public double? MiniTop { get; set; }

    public bool AutoCapitalize { get; set; } = true;
    public bool DoubleSpacePeriod { get; set; } = true;
    public bool SmartPunctuation { get; set; } = true;
    public bool AutoExpandOnSpace { get; set; } = true;
    public bool SuggestPhrases { get; set; }
    public bool LearningPaused { get; set; }
    public bool ProtectPasswords { get; set; } = true;
    /// <summary>The Stickers key; its picture search is the only thing that uses the internet.</summary>
    public bool StickersEnabled { get; set; } = true;
    /// <summary>Holding a suggestion offers to forget that word.</summary>
    public bool HoldToForget { get; set; } = true;
    /// <summary>Resting the pointer on a key presses it (dwell clicking).</summary>
    public bool HoverToPress { get; set; }
    public double HoverSeconds { get; set; } = 1.0;
    /// <summary>One of <see cref="SwipeModes"/>.</summary>
    public string SwipeMode { get; set; } = SwipeModes.Hold;

    public bool KeySounds { get; set; } = true;
    /// <summary>0 to 100.</summary>
    public int KeySoundVolume { get; set; } = 70;

    public List<ExpansionItem> Expansions { get; set; } = new()
    {
        new() { Abbreviation = "brb", Expansion = "be right back" },
        new() { Abbreviation = "omw", Expansion = "on my way" },
        new() { Abbreviation = "ty", Expansion = "thank you" },
        new() { Abbreviation = "tyvm", Expansion = "thank you very much" },
        new() { Abbreviation = "idk", Expansion = "I don't know" },
        new() { Abbreviation = "lmk", Expansion = "let me know" },
        new() { Abbreviation = "btw", Expansion = "by the way" },
        new() { Abbreviation = "np", Expansion = "no problem" },
        new() { Abbreviation = "imo", Expansion = "in my opinion" },
        new() { Abbreviation = "asap", Expansion = "as soon as possible" },
        new() { Abbreviation = "pls", Expansion = "please" },
        new() { Abbreviation = "thx", Expansion = "thanks" },
        new() { Abbreviation = "afaik", Expansion = "as far as I know" },
    };

    static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath)) ?? new AppSettings();
        }
        catch (Exception)
        {
            try { File.Copy(SettingsPath, SettingsPath + ".bad", overwrite: true); } catch (IOException) { }
        }
        return new AppSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(DataDir);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, JsonOptions));
    }
}

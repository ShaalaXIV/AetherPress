using Dalamud.Configuration;

namespace PenumbraTextureWatcher;

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    internal const int CurrentVersion = 3;

    public int Version { get; set; } = CurrentVersion;
    public string PenumbraPath { get; set; } = "";
    public bool AutomaticallyCompressNewMods { get; set; } = false;
    public bool SkipSkinTextures { get; set; } = true;
    public string BaseScale { get; set; } = "2K";
    public string BaseFormat { get; set; } = "BC7";
    public string BaseFilter { get; set; } = "Bicubic";
    public string NormalScale { get; set; } = "4K";
    public string NormalFormat { get; set; } = "8.8.8.8 BGRA";
    public string NormalFilter { get; set; } = "Bicubic";
    public string MaskScale { get; set; } = "2K";
    public string MaskFormat { get; set; } = "BC7";
    public string MaskFilter { get; set; } = "Bicubic";

    internal bool Migrate()
    {
        if (Version == CurrentVersion) return false;

        // Reset configurations from the adaptive/Smart builds to the measured
        // Balanced recommendation.
        BaseScale = "2K";
        BaseFormat = "BC7";
        BaseFilter = "Bicubic";
        NormalScale = "4K";
        NormalFormat = "8.8.8.8 BGRA";
        NormalFilter = "Bicubic";
        MaskScale = "2K";
        MaskFormat = "BC7";
        MaskFilter = "Bicubic";
        Version = CurrentVersion;
        return true;
    }

    internal OrganizerAndCompression.CompressionSettings ToCompressionSettings() => new()
    {
        Base = new(BaseScale, BaseFormat, BaseFilter),
        Normal = new(NormalScale, NormalFormat, NormalFilter),
        Mask = new(MaskScale, MaskFormat, MaskFilter)
    };
}

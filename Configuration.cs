using Dalamud.Configuration;

namespace PenumbraTextureWatcher;

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; }
    public string PenumbraPath { get; set; } = "";
    public bool AutomaticallyCompressNewMods { get; set; } = false;
    public bool SkipSkinTextures { get; set; } = true;
    public bool AdaptiveResolution { get; set; } = true;
    public string AdaptiveProfile { get; set; } = "Quality";
    public string CompressionPreset { get; set; } = "Balanced";
    public string BaseScale { get; set; } = "2K";
    public string BaseFormat { get; set; } = "Smart";
    public string BaseFilter { get; set; } = "Bicubic";
    public string NormalScale { get; set; } = "4K";
    public string NormalFormat { get; set; } = "Smart";
    public string NormalFilter { get; set; } = "Bicubic";
    public string MaskScale { get; set; } = "1K";
    public string MaskFormat { get; set; } = "Smart";
    public string MaskFilter { get; set; } = "Bicubic";

    internal OrganizerAndCompression.CompressionSettings ToCompressionSettings() => new()
    {
        Base = new(BaseScale, BaseFormat, BaseFilter),
        Normal = new(NormalScale, NormalFormat, NormalFilter),
        Mask = new(MaskScale, MaskFormat, MaskFilter),
        AdaptiveResolution = AdaptiveResolution,
        AdaptiveProfile = AdaptiveProfile
    };
}

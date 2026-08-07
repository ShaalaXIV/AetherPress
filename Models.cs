using System.Text.Json.Serialization;

namespace OrganizerAndCompression;

sealed class AppState
{
    public string LibraryPath { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OrganizerAndCompression", "Library");
    public string DeploymentPath { get; set; } = "";
    public string TexconvPath { get; set; } = "";
    public string PenumbraConfigPath { get; set; } = "";
    public string PenumbraModRoot { get; set; } = "";
    public List<string> PenumbraFolders { get; set; } = [];
    public List<string> PenumbraFoldersBaseline { get; set; } = [];
    public string ActiveProfile { get; set; } = "Default";
    public List<ModEntry> Mods { get; set; } = [];
    public List<ProfileState> Profiles { get; set; } = [new()];
    public CompressionSettings Compression { get; set; } = new();
    public bool UseEmbeddedXmaLogin { get; set; }
    public int TextScalePercent { get; set; } = 115;
}

sealed class ProfileState
{
    public string Name { get; set; } = "Default";
    public List<string> EnabledModIds { get; set; } = [];
    public List<string> Priority { get; set; } = [];
}

sealed class ModEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Unnamed Mod";
    public string Version { get; set; } = "";
    public string FolderName { get; set; } = "";
    public string Source { get; set; } = "";
    public string SourceKind { get; set; } = "Managed";
    public string PhysicalPath { get; set; } = "";
    public string Author { get; set; } = "";
    public string Website { get; set; } = "";
    public string Description { get; set; } = "";
    public List<string> Tags { get; set; } = [];
    public string PreviewPath { get; set; } = "";
    public string VirtualFolder { get; set; } = "";
    public string ProposedVirtualFolder { get; set; } = "";
    public DateTime InstalledUtc { get; set; } = DateTime.UtcNow;
    public string PreviewStatus { get; set; } = "Not scraped";
    public DateTime? PreviewAttemptUtc { get; set; }
    public DateTime? PreviewScrapedUtc { get; set; }
    public string PreviewSourceUrl { get; set; } = "";
    public string PreviewLastError { get; set; } = "";
    public string CompressionStatus { get; set; } = "Not run";
    public DateTime? TexturesDownscaledUtc { get; set; }
    public string CompressionSummary { get; set; } = "";
    public string CompressionLastError { get; set; } = "";
    [JsonIgnore] public long SizeBytes { get; set; }
    [JsonIgnore] public int FileCount { get; set; }
    [JsonIgnore] public int ConflictCount { get; set; }
}

sealed class CompressionSettings
{
    public TextureRule Base { get; set; } = new("2K", "BC7", "Bicubic");
    public TextureRule Normal { get; set; } = new("4K", "8.8.8.8 BGRA", "Bicubic");
    public TextureRule Mask { get; set; } = new("1K", "BC7", "Bicubic");
    public bool AdaptiveResolution { get; set; }
    public string AdaptiveProfile { get; set; } = "Quality";
}

sealed record TextureRule(string Scale, string Format, string Filter);
sealed record ConflictInfo(string RelativePath, IReadOnlyList<string> ModNames, string Winner);
sealed record CompressionProgress(int Completed, int Total, string Message);

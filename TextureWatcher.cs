using Dalamud.Plugin.Services;
using OrganizerAndCompression;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PenumbraTextureWatcher;

sealed class TextureWatcher : IDisposable
{
    readonly Configuration configuration;
    readonly string texconvPath;
    readonly IPluginLog log;
    readonly SemaphoreSlim compressionLock = new(1, 1);
    readonly CancellationTokenSource cancellation = new();
    bool disposed;

    public event Action<string>? NewModDetected;
    public event Action<CompressionOutcome>? CompressionCompleted;
    public string Status { get; private set; } = "Stopped";
    public string LastResult { get; private set; } = "No compression has run yet.";

    public TextureWatcher(Configuration configuration, string texconvPath, IPluginLog log)
    {
        this.configuration = configuration; this.texconvPath = texconvPath; this.log = log;
        Restart();
    }

    public void Restart()
    {
        if (!Directory.Exists(configuration.PenumbraPath)) { Status = "Penumbra folder not found"; return; }
        if (!File.Exists(texconvPath)) { Status = "texconv.exe is missing"; return; }
        Status = configuration.AutomaticallyCompressNewMods ? "Ready - automatic compression on" : "Ready - prompts enabled";
        log.Information("Listening for completed Penumbra mod installs at {Path}", configuration.PenumbraPath);
    }

    public void HandlePenumbraModAdded(string baseDirectoryName)
    {
        if (disposed || string.IsNullOrWhiteSpace(baseDirectoryName) || !Directory.Exists(configuration.PenumbraPath)) return;
        try
        {
            var penumbraRoot = Path.GetFullPath(configuration.PenumbraPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var modPath = Path.GetFullPath(Path.Combine(penumbraRoot, baseDirectoryName));
            if (!modPath.StartsWith(penumbraRoot, StringComparison.OrdinalIgnoreCase)
                || !Directory.Exists(modPath)
                || !File.Exists(Path.Combine(modPath, "meta.json")))
            {
                log.Warning("Ignoring invalid Penumbra ModAdded path {Directory}", baseDirectoryName);
                return;
            }
            log.Information("Penumbra reported completed mod install {Mod}", baseDirectoryName);
            if (IsAlreadyOptimized(modPath))
            {
                Status = $"Skipped already optimized: {Path.GetFileName(modPath)}";
                log.Information("Skipping {Mod} because its metadata is tagged Optimized by AetherPress", baseDirectoryName);
                return;
            }
            if (configuration.AutomaticallyCompressNewMods) _ = CompressModAsync(modPath);
            else
            {
                Status = $"Awaiting choice: {Path.GetFileName(modPath)}";
                NewModDetected?.Invoke(modPath);
            }
        }
        catch (Exception ex) { log.Error(ex, "Could not process Penumbra ModAdded path {Directory}", baseDirectoryName); }
    }

    public Task CompressModAsync(string modPath) => CompressManyAsync([modPath], "mod");
    public Task CompressAllAsync() => CompressManyAsync(Directory.Exists(configuration.PenumbraPath)
        ? Directory.EnumerateDirectories(configuration.PenumbraPath).OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase).ToList()
        : [], "Penumbra library");

    async Task CompressManyAsync(IReadOnlyList<string> modPaths, string scope)
    {
        if (!await compressionLock.WaitAsync(0)) { Status = "Compression is already running"; return; }
        try
        {
            var succeeded = 0; var failed = 0;
            for (var index = 0; index < modPaths.Count; index++)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                var modPath = modPaths[index]; var name = Path.GetFileName(modPath);
                if (IsAlreadyOptimized(modPath))
                {
                    Status = $"[{index + 1}/{modPaths.Count}] Skipped already optimized: {name}";
                    log.Information("Skipping {Mod} because its metadata is tagged Optimized by AetherPress", name);
                    continue;
                }
                Status = $"[{index + 1}/{modPaths.Count}] Compressing {name}";
                try
                {
                    var progress = new Progress<CompressionProgress>(p => Status = $"[{index + 1}/{modPaths.Count}] {name}: {p.Message}");
                    var result = await CompressionEngine.CompressFolderAsync(modPath, texconvPath, configuration.ToCompressionSettings(), progress, cancellation.Token, configuration.SkipSkinTextures);
                    succeeded++; LastResult = $"{name}: {result}"; log.Information("{Result}", LastResult);
                    var counts = ParseCounts(result);
                    if (counts.Compressed > 0) AppendOptimizedDescription(modPath);
                    CompressionCompleted?.Invoke(new CompressionOutcome(name, counts.Compressed, counts.Matching, true, ""));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    failed++; LastResult = $"{name}: {ex.Message}"; log.Error(ex, "Texture compression failed for {Mod}", name);
                    CompressionCompleted?.Invoke(new CompressionOutcome(name, 0, 0, false, ex.Message));
                }
            }
            Status = $"Finished {scope}: {succeeded} succeeded, {failed} failed";
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        finally { compressionLock.Release(); }
    }

    static (int Compressed, int Matching) ParseCounts(string result)
    {
        const string prefix = "Compressed ";
        if (!result.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return (0, 0);
        var words = result[prefix.Length..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Length >= 3 && int.TryParse(words[0], out var compressed) && int.TryParse(words[2], out var matching)
            ? (compressed, matching) : (0, 0);
    }

    void AppendOptimizedDescription(string modPath)
    {
        var metadataPath = Path.Combine(modPath, "meta.json");
        try
        {
            if (!File.Exists(metadataPath)) return;
            var root = JsonNode.Parse(File.ReadAllText(metadataPath)) as JsonObject;
            if (root is null) return;
            const string tag = "Optimized by AetherPress";
            var description = root["Description"]?.GetValue<string>() ?? "";
            if (description.Contains(tag, StringComparison.OrdinalIgnoreCase)) return;
            root["Description"] = string.IsNullOrWhiteSpace(description) ? tag : description.TrimEnd() + Environment.NewLine + Environment.NewLine + tag;
            File.WriteAllText(metadataPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            log.Information("Added AetherPress optimization tag to {Metadata}", metadataPath);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            log.Warning(ex, "Could not update AetherPress optimization tag in {Metadata}", metadataPath);
        }
    }

    static bool IsAlreadyOptimized(string modPath)
    {
        var metadataPath = Path.Combine(modPath, "meta.json");
        try
        {
            if (!File.Exists(metadataPath)) return false;
            using var document = JsonDocument.Parse(File.ReadAllText(metadataPath));
            return document.RootElement.TryGetProperty("Description", out var description)
                && description.ValueKind == JsonValueKind.String
                && (description.GetString() ?? "").Contains("Optimized by AetherPress", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { return false; }
    }

    public void Dispose()
    {
        disposed = true; cancellation.Cancel(); cancellation.Dispose(); compressionLock.Dispose();
    }
}

sealed record CompressionOutcome(string ModName, int Compressed, int Matching, bool Success, string Error);

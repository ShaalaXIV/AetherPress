using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;

namespace PenumbraTextureWatcher;

/// <summary>
/// Watches the local player's resolved Penumbra resources and queues the mod
/// folders that supply newly equipped textures. The compression engine remains
/// responsible for deciding which files actually need work and for skin skips.
/// </summary>
sealed class EquippedTextureMonitor : IDisposable
{
    static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);
    static readonly TimeSpan SettleDelay = TimeSpan.FromSeconds(2);

    readonly Configuration configuration;
    readonly TextureWatcher watcher;
    readonly IPluginLog log;
    readonly ICallGateSubscriber<Dictionary<ushort, Dictionary<string, HashSet<string>>>> getPlayerResourcePaths;
    readonly HashSet<string> queuedMods = new(StringComparer.OrdinalIgnoreCase);

    DateTime nextPollUtc;
    DateTime pendingSinceUtc;
    string pendingSignature = "";
    string processedSignature = "";
    IReadOnlyList<string> pendingModRoots = [];
    bool ipcWarningLogged;
    bool disposed;

    public EquippedTextureMonitor(
        Configuration configuration,
        TextureWatcher watcher,
        IDalamudPluginInterface pluginInterface,
        IPluginLog log)
    {
        this.configuration = configuration;
        this.watcher = watcher;
        this.log = log;
        getPlayerResourcePaths = pluginInterface.GetIpcSubscriber<Dictionary<ushort, Dictionary<string, HashSet<string>>>>(
            "Penumbra.GetPlayerResourcePaths.V5");
    }

    public void Restart()
    {
        nextPollUtc = DateTime.MinValue;
        pendingSinceUtc = DateTime.MinValue;
        pendingSignature = "";
        processedSignature = "";
        pendingModRoots = [];
        queuedMods.Clear();
        ipcWarningLogged = false;
    }

    public void Update()
    {
        if (disposed || !configuration.AutomaticallyCompressEquippedItems)
            return;

        var now = DateTime.UtcNow;
        if (now < nextPollUtc)
            return;
        nextPollUtc = now + PollInterval;

        try
        {
            var resources = getPlayerResourcePaths.InvokeFunc();
            if (!resources.TryGetValue(0, out var playerResources))
                return;

            ipcWarningLogged = false;
            var modRoots = FindContributingModRoots(playerResources.Keys, configuration.PenumbraPath);
            var signature = string.Join('\n', modRoots);

            if (!string.Equals(signature, pendingSignature, StringComparison.Ordinal))
            {
                pendingSignature = signature;
                pendingModRoots = modRoots;
                pendingSinceUtc = now;
                return;
            }

            if (now - pendingSinceUtc < SettleDelay
                || string.Equals(signature, processedSignature, StringComparison.Ordinal))
                return;

            processedSignature = signature;
            var newlySeen = pendingModRoots.Where(queuedMods.Add).ToList();
            if (newlySeen.Count == 0)
                return;

            log.Information(
                "Equipped appearance settled; queued {Count} contributing Penumbra mod(s) for texture inspection: {Mods}",
                newlySeen.Count,
                string.Join(", ", newlySeen.Select(Path.GetFileName)));
            _ = watcher.CompressEquippedModsAsync(newlySeen);
        }
        catch (Exception ex)
        {
            if (ipcWarningLogged)
                return;
            ipcWarningLogged = true;
            log.Warning(ex, "Equipped-item optimization is waiting for Penumbra's resource-path API");
        }
    }

    static IReadOnlyList<string> FindContributingModRoots(IEnumerable<string> actualPaths, string penumbraPath)
    {
        if (string.IsNullOrWhiteSpace(penumbraPath) || !Directory.Exists(penumbraPath))
            return [];

        var root = Path.GetFullPath(penumbraPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var rootPrefix = root + Path.DirectorySeparatorChar;
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var actualPath in actualPaths)
        {
            if (string.IsNullOrWhiteSpace(actualPath)
                || !Path.IsPathFullyQualified(actualPath)
                || !IsTexture(actualPath))
                continue;

            string fullPath;
            try { fullPath = Path.GetFullPath(actualPath); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { continue; }

            if (!fullPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
                continue;

            var relative = Path.GetRelativePath(root, fullPath);
            var separator = relative.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]);
            if (separator <= 0)
                continue;

            var modRoot = Path.GetFullPath(Path.Combine(root, relative[..separator]));
            if (modRoot.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase)
                && File.Exists(Path.Combine(modRoot, "meta.json")))
                result.Add(modRoot);
        }

        return result.OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    static bool IsTexture(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".tex", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".dds", StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose() => disposed = true;
}

using System.Text.Json;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Interface.ImGuiNotification;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;

namespace PenumbraTextureWatcher;

public sealed class Plugin : IDalamudPlugin
{
    const string Command = "/aetherpress";
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static INotificationManager Notifications { get; private set; } = null!;

    internal Configuration Configuration { get; }
    internal TextureWatcher Watcher { get; }
    readonly WindowSystem windows = new("PenumbraTextureWatcher");
    readonly ConfigWindow configWindow;
    readonly ICallGateSubscriber<string, object?> penumbraModAdded;

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        if (string.IsNullOrWhiteSpace(Configuration.PenumbraPath)) Configuration.PenumbraPath = DetectPenumbraPath();
        var texconv = Path.Combine(PluginInterface.AssemblyLocation.DirectoryName!, "texconv.exe");
        Watcher = new TextureWatcher(Configuration, texconv, Log);
        Watcher.NewModDetected += OnNewModDetected;
        Watcher.CompressionCompleted += OnCompressionCompleted;
        penumbraModAdded = PluginInterface.GetIpcSubscriber<string, object?>("Penumbra.ModAdded");
        penumbraModAdded.Subscribe(Watcher.HandlePenumbraModAdded);
        configWindow = new ConfigWindow(this); windows.AddWindow(configWindow);
        CommandManager.AddHandler(Command, new CommandInfo((_, _) => configWindow.Toggle()) { HelpMessage = "Open AetherPress texture compression settings." });
        PluginInterface.UiBuilder.Draw += DrainNewModPrompts;
        PluginInterface.UiBuilder.Draw += windows.Draw;
        PluginInterface.UiBuilder.OpenConfigUi += configWindow.Toggle;
        PluginInterface.UiBuilder.OpenMainUi += configWindow.Toggle;
    }

    internal void SaveAndRestart()
    {
        PluginInterface.SavePluginConfig(Configuration); Watcher.Restart();
    }

    readonly Queue<string> newModPrompts = new();
    readonly Queue<CompressionOutcome> compressionNotifications = new();
    readonly object promptLock = new();
    void OnNewModDetected(string modPath) { lock (promptLock) newModPrompts.Enqueue(modPath); }
    void OnCompressionCompleted(CompressionOutcome outcome) { lock (promptLock) compressionNotifications.Enqueue(outcome); }
    void DrainNewModPrompts()
    {
        string? path = null;
        CompressionOutcome? outcome = null;
        lock (promptLock)
        {
            if (newModPrompts.Count > 0) path = newModPrompts.Dequeue();
            if (compressionNotifications.Count > 0) outcome = compressionNotifications.Dequeue();
        }
        if (path is not null)
        {
            Notifications.AddNotification(new Notification
            {
                Title = "New Penumbra mod detected",
                Content = $"{Path.GetFileName(path)} was installed. Choose Yes or No in AetherPress.",
                Type = NotificationType.Info,
                InitialDuration = TimeSpan.FromSeconds(12)
            });
            configWindow.PromptForNewMod(path);
        }
        if (outcome is not null)
        {
            Notifications.AddNotification(new Notification
            {
                Title = outcome.Success ? $"Successfully compressed \"{outcome.ModName}\"" : $"Compression failed: {outcome.ModName}",
                Content = outcome.Success ? $"{outcome.Compressed}/{outcome.Matching} textures" : outcome.Error,
                Type = outcome.Success ? NotificationType.Success : NotificationType.Error,
                InitialDuration = TimeSpan.FromSeconds(outcome.Success ? 8 : 15)
            });
        }
    }

    static string DetectPenumbraPath()
    {
        try
        {
            var configPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XIVLauncher", "pluginConfigs", "Penumbra.json");
            if (!File.Exists(configPath)) return "";
            using var json = JsonDocument.Parse(File.ReadAllText(configPath));
            return json.RootElement.TryGetProperty("ModDirectory", out var root) ? root.GetString() ?? "" : "";
        }
        catch { return ""; }
    }

    public void Dispose()
    {
        penumbraModAdded.Unsubscribe(Watcher.HandlePenumbraModAdded);
        Watcher.NewModDetected -= OnNewModDetected;
        Watcher.CompressionCompleted -= OnCompressionCompleted;
        PluginInterface.UiBuilder.Draw -= DrainNewModPrompts; PluginInterface.UiBuilder.Draw -= windows.Draw; PluginInterface.UiBuilder.OpenConfigUi -= configWindow.Toggle; PluginInterface.UiBuilder.OpenMainUi -= configWindow.Toggle;
        CommandManager.RemoveHandler(Command); windows.RemoveAllWindows(); configWindow.Dispose(); Watcher.Dispose();
    }
}

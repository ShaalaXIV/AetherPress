using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace PenumbraTextureWatcher;

sealed class ConfigWindow : Window, IDisposable
{
    static readonly string[] Scales = ["4K", "2K", "1K", "512"];
    static readonly string[] Formats = ["BC7", "BC5", "8.8.8.8 BGRA"];
    static readonly string[] Filters = ["Bilinear", "Bicubic", "Nearest Neighbor"];
    readonly Plugin plugin;
    readonly Queue<string> prompts = new();
    string? activePrompt;
    bool openCompressAllConfirmation;

    public ConfigWindow(Plugin plugin) : base("AetherPress###AetherPressSettings")
    {
        this.plugin = plugin;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(720, 420), MaximumSize = new Vector2(float.MaxValue) };
    }

    public override void Draw()
    {
        var config = plugin.Configuration;
        ImGui.TextUnformatted($"Status: {plugin.Watcher.Status}");
        ImGui.TextWrapped($"Last result: {plugin.Watcher.LastResult}");
        ImGui.Separator();

        ImGui.TextUnformatted("Compression Preset");
        ImGui.Separator(); ImGui.Spacing();
        if (ImGui.BeginTable("CompressionPresets", 4, ImGuiTableFlags.SizingStretchSame))
        {
            DrawPreset(config, "Quality", "Best visual quality with moderate savings", "4K", "4K", "8.8.8.8 BGRA", "2K");
            DrawPreset(config, "Balanced (Recommended)", "Strong savings with minimal visible quality loss", "2K", "4K", "8.8.8.8 BGRA", "2K");
            DrawPreset(config, "Smaller Files", "More compression with mild normal-map softening", "2K", "2K", "8.8.8.8 BGRA", "2K");
            DrawPreset(config, "Maximum Savings", "Smallest tested 2K files; more fine-detail loss", "2K", "2K", "BC7", "2K");
            ImGui.EndTable();
        }

        ImGui.Spacing();
        var skipSkin = config.SkipSkinTextures;
        if (ImGui.Checkbox("Protect skin textures", ref skipSkin)) config.SkipSkinTextures = skipSkin;
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Skips redirects to chara/human/ and chara/bibo* found in Penumbra JSON files.");

        ImGui.Spacing();
        ImGui.TextUnformatted("Penumbra folder"); ImGui.SameLine();
        ImGui.SetNextItemWidth(-1);
        var path = config.PenumbraPath;
        if (ImGui.InputText("##PenumbraPath", ref path, 1024)) config.PenumbraPath = path;
        ImGui.Spacing();
        if (ImGui.Button("Save Settings", new Vector2(180, 0))) plugin.SaveAndRestart();

        ImGui.Spacing(); ImGui.Separator(); ImGui.Spacing();
        ImGui.TextUnformatted("Newly Installed Mods");
        var automatic = config.AutomaticallyCompressNewMods;
        if (ImGui.RadioButton("Ask before optimizing", !automatic)) config.AutomaticallyCompressNewMods = false;
        ImGui.SameLine();
        if (ImGui.RadioButton("Optimize automatically", automatic)) config.AutomaticallyCompressNewMods = true;
        ImGui.Spacing();
        if (ImGui.Button("Optimize Entire Penumbra Folder...", new Vector2(240, 0))) openCompressAllConfirmation = true;

        ImGui.Spacing(); ImGui.Separator(); ImGui.Spacing();
        if (ImGui.CollapsingHeader("Advanced Settings")) DrawAdvancedSettings(config);

        DrawNewModPrompt();
        DrawCompressAllConfirmation();
    }

    public void PromptForNewMod(string modPath)
    {
        prompts.Enqueue(modPath); IsOpen = true;
    }

    void DrawNewModPrompt()
    {
        if (activePrompt is null && prompts.Count > 0)
        {
            activePrompt = prompts.Dequeue(); ImGui.OpenPopup("New Penumbra mod detected");
        }
        var open = true;
        if (!ImGui.BeginPopupModal("New Penumbra mod detected", ref open, ImGuiWindowFlags.AlwaysAutoResize)) return;
        ImGui.TextWrapped($"{Path.GetFileName(activePrompt)} was installed. Would you like to compress it using the current AetherPress settings?");
        if (ImGui.Button("Yes", new Vector2(110, 0)))
        {
            var path = activePrompt; activePrompt = null; ImGui.CloseCurrentPopup();
            if (path is not null) _ = plugin.Watcher.CompressModAsync(path);
        }
        ImGui.SameLine();
        if (ImGui.Button("No", new Vector2(110, 0))) { activePrompt = null; ImGui.CloseCurrentPopup(); }
        ImGui.EndPopup();
    }

    void DrawCompressAllConfirmation()
    {
        if (openCompressAllConfirmation) { ImGui.OpenPopup("Compress entire Penumbra folder?"); openCompressAllConfirmation = false; }
        var open = true;
        if (!ImGui.BeginPopupModal("Compress entire Penumbra folder?", ref open, ImGuiWindowFlags.AlwaysAutoResize)) return;
        ImGui.TextWrapped("This will recursively process every mod folder and replace eligible textures in place. Continue?");
        if (ImGui.Button("Compress All", new Vector2(130, 0))) { ImGui.CloseCurrentPopup(); _ = plugin.Watcher.CompressAllAsync(); }
        ImGui.SameLine();
        if (ImGui.Button("Cancel", new Vector2(110, 0))) ImGui.CloseCurrentPopup();
        ImGui.EndPopup();
    }

    static void DrawAdvancedSettings(Configuration config)
    {
        ImGui.Spacing();
        ImGui.TextUnformatted("Exact Texture Rules");
        ImGui.Separator(); ImGui.Spacing();
        if (ImGui.BeginTable("CompressionRules", 4))
        {
            ImGui.TableSetupColumn("Texture"); ImGui.TableSetupColumn("Maximum Size"); ImGui.TableSetupColumn("Format"); ImGui.TableSetupColumn("Filter");
            ImGui.TableHeadersRow();
            (config.BaseScale, config.BaseFormat, config.BaseFilter) = DrawRule("Base / Diffuse", config.BaseScale, config.BaseFormat, config.BaseFilter);
            (config.NormalScale, config.NormalFormat, config.NormalFilter) = DrawRule("Normal", config.NormalScale, config.NormalFormat, config.NormalFilter);
            (config.MaskScale, config.MaskFormat, config.MaskFilter) = DrawRule("Mask", config.MaskScale, config.MaskFormat, config.MaskFilter);
            ImGui.EndTable();
        }

        ImGui.Spacing();
        ImGui.TextWrapped("Selected sizes are hard maximums; AetherPress never silently chooses a smaller size.");
        if (config.NormalFormat == "BC5")
            ImGui.TextWrapped("Warning: BC5 discards channels used by some normal maps and can cause severe visible corruption.");
    }

    static (string Scale, string Format, string Filter) DrawRule(string label, string scale, string format, string filter)
    {
        ImGui.TableNextRow(); ImGui.TableNextColumn(); ImGui.TextUnformatted(label);
        ImGui.TableNextColumn(); Combo($"##{label}Scale", ref scale, Scales);
        ImGui.TableNextColumn(); Combo($"##{label}Format", ref format, Formats);
        ImGui.TableNextColumn(); Combo($"##{label}Filter", ref filter, Filters);
        return (scale, format, filter);
    }

    static void DrawPreset(Configuration config, string name, string description, string baseScale, string normalScale, string normalFormat, string maskScale)
    {
        ImGui.TableNextColumn();
        var selected = MatchesPreset(config, baseScale, normalScale, normalFormat, maskScale);
        if (ImGui.RadioButton($"{name}##Preset{name}", selected))
            ApplyPreset(config, baseScale, normalScale, normalFormat, maskScale);
        ImGui.TextWrapped(description);
        ImGui.Spacing();
    }

    static bool MatchesPreset(Configuration config, string baseScale, string normalScale, string normalFormat, string maskScale) =>
        config.BaseScale == baseScale && config.BaseFormat == "BC7" && config.BaseFilter == "Bicubic" &&
        config.NormalScale == normalScale && config.NormalFormat == normalFormat && config.NormalFilter == "Bicubic" &&
        config.MaskScale == maskScale && config.MaskFormat == "BC7" && config.MaskFilter == "Bicubic";

    static void ApplyPreset(Configuration config, string baseScale, string normalScale, string normalFormat, string maskScale)
    {
        config.BaseScale = baseScale;
        config.BaseFormat = "BC7";
        config.BaseFilter = "Bicubic";
        config.NormalScale = normalScale;
        config.NormalFormat = normalFormat;
        config.NormalFilter = "Bicubic";
        config.MaskScale = maskScale;
        config.MaskFormat = "BC7";
        config.MaskFilter = "Bicubic";
    }

    static void Combo(string id, ref string value, string[] choices)
    {
        ImGui.SetNextItemWidth(-1);
        if (!ImGui.BeginCombo(id, value)) return;
        foreach (var choice in choices)
            if (ImGui.Selectable(choice, choice == value)) value = choice;
        ImGui.EndCombo();
    }

    public void Dispose() { }
}

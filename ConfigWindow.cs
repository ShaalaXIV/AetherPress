using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace PenumbraTextureWatcher;

sealed class ConfigWindow : Window, IDisposable
{
    static readonly string[] Scales = ["4K", "2K", "1K", "512"];
    static readonly string[] Formats = ["Smart", "BC7", "BC5", "8.8.8.8 BGRA"];
    static readonly string[] Filters = ["Bilinear", "Bicubic", "Nearest Neighbor"];
    static readonly string[] Profiles = ["Quality", "Balanced", "Aggressive"];
    readonly Plugin plugin;
    readonly Queue<string> prompts = new();
    string? activePrompt;
    bool openCompressAllConfirmation;

    public ConfigWindow(Plugin plugin) : base("AetherPress###AetherPressSettings")
    {
        this.plugin = plugin;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(600, 390), MaximumSize = new Vector2(float.MaxValue) };
    }

    public override void Draw()
    {
        var config = plugin.Configuration;
        ImGui.TextUnformatted($"Status: {plugin.Watcher.Status}");
        ImGui.TextWrapped($"Last result: {plugin.Watcher.LastResult}");
        ImGui.Separator();

        ImGui.TextUnformatted("Compression Preset");
        ImGui.Separator(); ImGui.Spacing();
        if (!PresetMatches(config, config.CompressionPreset)) config.CompressionPreset = "Custom";
        if (ImGui.BeginTable("PresetCards", 3))
        {
            ImGui.TableNextColumn(); DrawPreset(config, "Quality", "Best visual quality, moderate savings");
            ImGui.TableNextColumn(); DrawPreset(config, "Balanced", "Strong savings with minimal visible quality loss");
            ImGui.TableNextColumn(); DrawPreset(config, "Maximum Savings", "More aggressive optimization");
            ImGui.EndTable();
        }

        ImGui.Spacing();
        var smart = config.BaseFormat == "Smart" && config.NormalFormat == "Smart" && config.MaskFormat == "Smart";
        if (ImGui.Checkbox("Automatically choose the best texture format", ref smart))
        {
            if (smart) config.BaseFormat = config.NormalFormat = config.MaskFormat = "Smart";
            else config.CompressionPreset = "Custom";
        }
        var skipSkin = config.SkipSkinTextures;
        if (ImGui.Checkbox("Protect skin textures", ref skipSkin)) config.SkipSkinTextures = skipSkin;
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Skips redirects to chara/human/ and chara/bibo* found in Penumbra JSON files.");
        var adaptive = config.AdaptiveResolution;
        if (ImGui.Checkbox("Adapt texture resolution to image detail", ref adaptive)) config.AdaptiveResolution = adaptive;

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
        if (ImGui.CollapsingHeader("Advanced Settings"))
        {
            ImGui.TextWrapped("Preset values can be adjusted below. Changing one marks the preset as Custom.");
            if (ImGui.BeginTable("CompressionRules", 4))
            {
                ImGui.TableSetupColumn("Texture"); ImGui.TableSetupColumn("Maximum Size"); ImGui.TableSetupColumn("Format"); ImGui.TableSetupColumn("Filter");
                ImGui.TableHeadersRow();
                var before = (config.BaseScale, config.BaseFormat, config.BaseFilter, config.NormalScale, config.NormalFormat, config.NormalFilter, config.MaskScale, config.MaskFormat, config.MaskFilter);
                (config.BaseScale, config.BaseFormat, config.BaseFilter) = DrawRule("Base / Diffuse", config.BaseScale, config.BaseFormat, config.BaseFilter);
                (config.NormalScale, config.NormalFormat, config.NormalFilter) = DrawRule("Normal", config.NormalScale, config.NormalFormat, config.NormalFilter);
                (config.MaskScale, config.MaskFormat, config.MaskFilter) = DrawRule("Mask", config.MaskScale, config.MaskFormat, config.MaskFilter);
                var after = (config.BaseScale, config.BaseFormat, config.BaseFilter, config.NormalScale, config.NormalFormat, config.NormalFilter, config.MaskScale, config.MaskFormat, config.MaskFilter);
                if (before != after) config.CompressionPreset = "Custom";
                ImGui.EndTable();
            }
        }

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

    static (string Scale, string Format, string Filter) DrawRule(string label, string scale, string format, string filter)
    {
        ImGui.TableNextRow(); ImGui.TableNextColumn(); ImGui.TextUnformatted(label);
        ImGui.TableNextColumn(); Combo($"##{label}Scale", ref scale, Scales);
        ImGui.TableNextColumn(); Combo($"##{label}Format", ref format, Formats);
        ImGui.TableNextColumn(); Combo($"##{label}Filter", ref filter, Filters);
        return (scale, format, filter);
    }

    static void DrawPreset(Configuration config, string name, string description)
    {
        var selected = config.CompressionPreset == name;
        if (ImGui.RadioButton(name, selected)) ApplyPreset(config, name);
        if (name == "Balanced") { ImGui.SameLine(); ImGui.TextDisabled("Recommended"); }
        ImGui.TextWrapped(description);
    }

    static void ApplyPreset(Configuration config, string name)
    {
        config.CompressionPreset = name;
        config.BaseFormat = config.NormalFormat = config.MaskFormat = "Smart";
        config.AdaptiveResolution = true;
        config.BaseFilter = config.NormalFilter = config.MaskFilter = "Bicubic";
        switch (name)
        {
            case "Quality":
                config.AdaptiveProfile = "Quality"; config.BaseScale = "4K"; config.NormalScale = "4K"; config.MaskScale = "2K"; break;
            case "Maximum Savings":
                config.AdaptiveProfile = "Aggressive"; config.BaseScale = "1K"; config.NormalScale = "2K"; config.MaskScale = "1K"; break;
            default:
                config.AdaptiveProfile = "Balanced"; config.BaseScale = "2K"; config.NormalScale = "4K"; config.MaskScale = "1K"; break;
        }
    }

    static bool PresetMatches(Configuration config, string name)
    {
        if (name == "Custom") return true;
        if (!config.AdaptiveResolution || config.BaseFormat != "Smart" || config.NormalFormat != "Smart" || config.MaskFormat != "Smart") return false;
        return name switch
        {
            "Quality" => config.AdaptiveProfile == "Quality" && config.BaseScale == "4K" && config.NormalScale == "4K" && config.MaskScale == "2K",
            "Maximum Savings" => config.AdaptiveProfile == "Aggressive" && config.BaseScale == "1K" && config.NormalScale == "2K" && config.MaskScale == "1K",
            "Balanced" => config.AdaptiveProfile == "Balanced" && config.BaseScale == "2K" && config.NormalScale == "4K" && config.MaskScale == "1K",
            _ => false
        };
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

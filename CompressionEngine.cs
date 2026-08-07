using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace OrganizerAndCompression;

enum TextureKind { Unknown, Base, Normal, Mask }
enum TextureContainer { Dds, FfxivTex }
enum DdsFormat { Unknown, Bc1, Bc3, Bc5, Bc7, Bgra }
sealed record TextureInfo(int Width, int Height, int Mips, DdsFormat Format, string Name, TextureContainer Container);
sealed record DdsInfo(int Width, int Height, int Mips, DdsFormat Format, string Name, int DataOffset, uint Dxgi);

static class CompressionEngine
{
    public static async Task<string> CompressFolderAsync(string root, string texconv, CompressionSettings settings, IProgress<CompressionProgress>? progress = null, CancellationToken cancellation = default, bool skipSkinTextures = false)
    {
        if (!File.Exists(texconv)) throw new FileNotFoundException("texconv.exe was not found.", texconv);
        var skippedRedirectFiles = skipSkinTextures ? FindHumanRedirectFiles(root) : [];
        var candidates = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(IsTexture).Select(path => (Path: path, Kind: Classify(Path.GetFileNameWithoutExtension(path))))
            .Where(x => !skippedRedirectFiles.Contains(Path.GetFullPath(x.Path)))
            .Where(x => x.Kind != TextureKind.Unknown).OrderBy(x => x.Path).ToList();
        if (candidates.Count == 0) return "No matching Base/Diffuse, Normal, or Mask textures were found.";

        var stage = Path.Combine(Path.GetTempPath(), "organizer-compression", Guid.NewGuid().ToString("N"));
        var outputs = new List<(string Staged, string Original)>();
        var skipped = 0;
        Directory.CreateDirectory(stage);
        try
        {
            for (var i = 0; i < candidates.Count; i++)
            {
                cancellation.ThrowIfCancellationRequested();
                var item = candidates[i];
                var relative = Path.GetRelativePath(root, item.Path);
                progress?.Report(new CompressionProgress(i, candidates.Count, $"Analyzing {relative}"));
                try
                {
                    var info = ReadInfo(item.Path);
                    if (info is null) throw new InvalidDataException("Texture header could not be read.");
                    var rule = item.Kind switch { TextureKind.Base => settings.Base, TextureKind.Normal => settings.Normal, _ => settings.Mask };
                    var maxSide = rule.Scale switch { "4K" => 4096, "2K" => 2048, "1K" => 1024, _ => 512 };
                    var format = ResolveFormat(item.Kind, info, rule.Format);
                    var filter = rule.Filter switch { "Bilinear" => "LINEAR", "Nearest Neighbor" => "POINT", _ => "CUBIC" };
                    var (width, height) = Resize(info.Width, info.Height, maxSide);
                    if (settings.AdaptiveResolution)
                    {
                        progress?.Report(new CompressionProgress(i, candidates.Count, $"Quality-testing {relative}"));
                        (width, height) = await DetermineAdaptiveSizeAsync(texconv, item.Path, info, width, height, item.Kind, settings.AdaptiveProfile, stage, cancellation);
                    }
                    if (width == info.Width && height == info.Height && info.Format == format.Item2) continue;

                    var outputDir = Path.Combine(stage, Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(outputDir);
                    progress?.Report(new CompressionProgress(i, candidates.Count, $"Compressing {relative}"));
                    var converted = await ConvertAsync(texconv, item.Path, outputDir, width, height, format.Item1, filter, cancellation);
                    outputs.Add((converted, item.Path));
                }
                catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or IOException)
                {
                    skipped++;
                    progress?.Report(new CompressionProgress(i, candidates.Count, $"Skipped {relative}: {ex.Message}"));
                }
            }
            foreach (var output in outputs) File.Copy(output.Staged, output.Original, true);
            progress?.Report(new CompressionProgress(candidates.Count, candidates.Count, $"Compressed {outputs.Count} texture(s)."));
            return $"Compressed {outputs.Count} of {candidates.Count} matching texture(s)." + (skipped > 0 ? $" Skipped {skipped} unreadable or unsupported texture(s)." : "");
        }
        finally { if (Directory.Exists(stage)) Directory.Delete(stage, true); }
    }

    public static async Task<string> CompressPmpAsync(string input, string output, string texconv, CompressionSettings settings, IProgress<CompressionProgress>? progress = null, CancellationToken cancellation = default)
    {
        var stage = Path.Combine(Path.GetTempPath(), "organizer-pmp", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        try
        {
            ZipFile.ExtractToDirectory(input, stage, true);
            var result = await CompressFolderAsync(stage, texconv, settings, progress, cancellation);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
            if (File.Exists(output)) File.Delete(output);
            ZipFile.CreateFromDirectory(stage, output, CompressionLevel.Optimal, false);
            return result + $" Repacked {Path.GetFileName(output)}.";
        }
        finally { if (Directory.Exists(stage)) Directory.Delete(stage, true); }
    }

    static async Task<string> ConvertAsync(string texconv, string input, string outputDir, int width, int height, string format, string filter, CancellationToken cancellation)
    {
        if (Path.GetExtension(input).Equals(".tex", StringComparison.OrdinalIgnoreCase))
        {
            var sourceDds = Path.Combine(outputDir, Path.GetFileNameWithoutExtension(input) + ".source.dds");
            if (!FfxivTexBridge.TryWriteDds(input, sourceDds, out var error)) throw new InvalidDataException(error);
            var convertedDds = await RunTexconv(texconv, sourceDds, outputDir, width, height, format, filter, cancellation);
            var outputTex = Path.Combine(outputDir, Path.GetFileName(input));
            if (!FfxivTexBridge.TryWriteTex(convertedDds, outputTex, out error)) throw new InvalidDataException(error);
            return outputTex;
        }
        return await RunTexconv(texconv, input, outputDir, width, height, format, filter, cancellation);
    }

    static async Task<string> RunTexconv(string texconv, string input, string outputDir, int width, int height, string format, string filter, CancellationToken cancellation)
    {
        var psi = new ProcessStartInfo(texconv)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            Arguments = $"-nologo -y -sepalpha -dx10 -m 0 -if {filter} -f {format}{(format == "BC7_UNORM" ? " -bc x" : "")} -w {width} -h {height} -o \"{outputDir}\" \"{input}\""
        };
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Could not start texconv.");
        var stdout = process.StandardOutput.ReadToEndAsync(cancellation);
        var stderr = process.StandardError.ReadToEndAsync(cancellation);
        await process.WaitForExitAsync(cancellation);
        var message = (await stdout) + (await stderr);
        if (process.ExitCode != 0) throw new InvalidOperationException($"texconv failed ({process.ExitCode}): {message}");
        var expected = Path.Combine(outputDir, Path.GetFileNameWithoutExtension(input) + ".dds");
        if (!File.Exists(expected)) throw new InvalidOperationException($"texconv did not create {expected}.");
        return expected;
    }

    static bool IsTexture(string path) => Path.GetExtension(path).Equals(".dds", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(path).Equals(".tex", StringComparison.OrdinalIgnoreCase);
    static (string Texconv, DdsFormat Dds) ResolveFormat(TextureKind kind, TextureInfo info, string requested)
    {
        if (!requested.Equals("Smart", StringComparison.OrdinalIgnoreCase))
            return requested switch { "BC5" => ("BC5_UNORM", DdsFormat.Bc5), "8.8.8.8 BGRA" => ("B8G8R8A8_UNORM", DdsFormat.Bgra), _ => ("BC7_UNORM", DdsFormat.Bc7) };

        // Retain already-efficient formats only where their channel limitations are known to be safe.
        // Everything else uses BC7 so smooth alpha and packed material channels remain available.
        if (info.Format == DdsFormat.Bc1 && kind is TextureKind.Base or TextureKind.Mask)
            return ("BC1_UNORM", DdsFormat.Bc1);
        if (info.Format == DdsFormat.Bc5 && kind == TextureKind.Normal)
            return ("BC5_UNORM", DdsFormat.Bc5);
        return ("BC7_UNORM", DdsFormat.Bc7);
    }

    static async Task<(int Width, int Height)> DetermineAdaptiveSizeAsync(string texconv, string input, TextureInfo info, int ceilingWidth, int ceilingHeight, TextureKind kind, string profile, string stage, CancellationToken cancellation)
    {
        var ceiling = Math.Max(ceilingWidth, ceilingHeight);
        var candidates = new[] { 2048, 1024, 512 }.Where(size => size < ceiling && size < Math.Max(info.Width, info.Height)).ToList();
        if (candidates.Count == 0) return (ceilingWidth, ceilingHeight);

        var analysisRoot = Path.Combine(stage, "analysis-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(analysisRoot);
        try
        {
            var source = input;
            if (Path.GetExtension(input).Equals(".tex", StringComparison.OrdinalIgnoreCase))
            {
                source = Path.Combine(analysisRoot, "source.dds");
                if (!FfxivTexBridge.TryWriteDds(input, source, out var error)) throw new InvalidDataException(error);
            }

            var originalDir = Path.Combine(analysisRoot, "original");
            Directory.CreateDirectory(originalDir);
            var originalDds = await RunTexconv(texconv, source, originalDir, info.Width, info.Height, "B8G8R8A8_UNORM", "CUBIC", cancellation);
            var original = ReadBgra(originalDds);
            var best = (Width: ceilingWidth, Height: ceilingHeight);
            var thresholds = AdaptiveThresholds(profile, kind);

            foreach (var side in candidates)
            {
                cancellation.ThrowIfCancellationRequested();
                var size = Resize(info.Width, info.Height, side);
                var downDir = Path.Combine(analysisRoot, "down-" + side);
                var upDir = Path.Combine(analysisRoot, "up-" + side);
                Directory.CreateDirectory(downDir); Directory.CreateDirectory(upDir);
                var down = await RunTexconv(texconv, originalDds, downDir, size.Width, size.Height, "B8G8R8A8_UNORM", "CUBIC", cancellation);
                var up = await RunTexconv(texconv, down, upDir, info.Width, info.Height, "B8G8R8A8_UNORM", "CUBIC", cancellation);
                var reconstructed = ReadBgra(up);
                var score = CompareBgra(original.Pixels, reconstructed.Pixels, info.Width, info.Height);
                if (score.Ssim < thresholds.Ssim || score.Rmse > thresholds.Rmse || score.GradientRatio < thresholds.GradientRatio) break;
                best = size;
            }
            return best;
        }
        catch (OperationCanceledException) { throw; }
        catch { return (ceilingWidth, ceilingHeight); }
    }

    static (double Ssim, double Rmse, double GradientRatio) AdaptiveThresholds(string profile, TextureKind kind)
    {
        var values = profile.ToLowerInvariant() switch
        {
            "aggressive" => (Ssim: 0.86, Rmse: 0.040, Gradient: 0.60),
            "balanced" => (Ssim: 0.90, Rmse: 0.030, Gradient: 0.70),
            _ => (Ssim: 0.92, Rmse: 0.020, Gradient: 0.75)
        };
        if (kind == TextureKind.Normal) values.Ssim = Math.Max(values.Ssim, profile.Equals("Quality", StringComparison.OrdinalIgnoreCase) ? 0.97 : values.Ssim + 0.03);
        return (values.Ssim, values.Rmse, values.Gradient);
    }

    static (byte[] Pixels, int Width, int Height) ReadBgra(string path)
    {
        var info = DdsReader.ReadRaw(path) ?? throw new InvalidDataException("Adaptive analysis DDS header could not be read.");
        if (info.Format != DdsFormat.Bgra) throw new InvalidDataException("Adaptive analysis did not produce BGRA pixels.");
        var length = checked(info.Width * info.Height * 4);
        using var stream = File.OpenRead(path);
        if (info.DataOffset + length > stream.Length) throw new InvalidDataException("Adaptive analysis DDS pixel data is truncated.");
        stream.Position = info.DataOffset;
        var pixels = new byte[length]; stream.ReadExactly(pixels);
        return (pixels, info.Width, info.Height);
    }

    static (double Ssim, double Rmse, double GradientRatio) CompareBgra(byte[] original, byte[] reconstructed, int width, int height)
    {
        if (original.Length != reconstructed.Length) return (0, 1, 0);
        var pixels = width * height;
        var worstSsim = 1.0; var worstRmse = 0.0;
        for (var channel = 0; channel < 4; channel++)
        {
            double sumA = 0, sumB = 0, sumAA = 0, sumBB = 0, sumAB = 0, error = 0;
            for (var i = channel; i < original.Length; i += 4)
            {
                var a = original[i] / 255.0; var b = reconstructed[i] / 255.0;
                sumA += a; sumB += b; sumAA += a * a; sumBB += b * b; sumAB += a * b;
                var d = a - b; error += d * d;
            }
            var meanA = sumA / pixels; var meanB = sumB / pixels;
            var varA = Math.Max(0, sumAA / pixels - meanA * meanA); var varB = Math.Max(0, sumBB / pixels - meanB * meanB);
            var covariance = sumAB / pixels - meanA * meanB;
            const double c1 = 0.0001, c2 = 0.0009;
            var ssim = ((2 * meanA * meanB + c1) * (2 * covariance + c2)) / ((meanA * meanA + meanB * meanB + c1) * (varA + varB + c2));
            worstSsim = Math.Min(worstSsim, ssim); worstRmse = Math.Max(worstRmse, Math.Sqrt(error / pixels));
        }
        var gradA = GradientEnergy(original, width, height); var gradB = GradientEnergy(reconstructed, width, height);
        return (worstSsim, worstRmse, gradA <= 1e-9 ? 1 : Math.Min(1, gradB / gradA));
    }

    static double GradientEnergy(byte[] pixels, int width, int height)
    {
        var step = Math.Max(1, (int)Math.Sqrt((double)(width * height) / 1_000_000));
        double energy = 0; long samples = 0;
        for (var y = 1; y < height - 1; y += step)
        for (var x = 1; x < width - 1; x += step)
        {
            double L(int px, int py) { var at = (py * width + px) * 4; return (pixels[at] * 0.114 + pixels[at + 1] * 0.587 + pixels[at + 2] * 0.299) / 255.0; }
            var gx = -L(x - 1, y - 1) + L(x + 1, y - 1) - 2 * L(x - 1, y) + 2 * L(x + 1, y) - L(x - 1, y + 1) + L(x + 1, y + 1);
            var gy = -L(x - 1, y - 1) - 2 * L(x, y - 1) - L(x + 1, y - 1) + L(x - 1, y + 1) + 2 * L(x, y + 1) + L(x + 1, y + 1);
            energy += Math.Sqrt(gx * gx + gy * gy); samples++;
        }
        return samples == 0 ? 0 : energy / samples;
    }
    static HashSet<string> FindHumanRedirectFiles(string root)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var jsonPath in Directory.EnumerateFiles(root, "*.json", SearchOption.AllDirectories))
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(jsonPath));
                ReadRedirects(document.RootElement, root, result);
            }
            catch (JsonException) { }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return result;
    }
    static void ReadRedirects(JsonElement element, string root, HashSet<string> result)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.NameEquals("Files") && property.Value.ValueKind == JsonValueKind.Object)
                {
                    foreach (var redirect in property.Value.EnumerateObject())
                    {
                        if (redirect.Value.ValueKind != JsonValueKind.String) continue;
                        var left = redirect.Name; var right = redirect.Value.GetString() ?? "";
                        if (IsSkippedGamePath(left)) AddLocalPath(right, root, result);
                        if (IsSkippedGamePath(right)) AddLocalPath(left, root, result);
                    }
                }
                ReadRedirects(property.Value, root, result);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var child in element.EnumerateArray()) ReadRedirects(child, root, result);
    }
    static bool IsSkippedGamePath(string path)
    {
        var normalized = path.Replace('\\', '/').TrimStart('/');
        return normalized.StartsWith("chara/human/", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("chara/bibo", StringComparison.OrdinalIgnoreCase);
    }
    static void AddLocalPath(string relative, string root, HashSet<string> result)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative)) return;
        var full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (full.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && File.Exists(full)) result.Add(full);
    }
    static TextureKind Classify(string name)
    {
        if (Token(name, "base") || Suffix(name, 'd') || Suffix(name, 's')) return TextureKind.Base;
        if (Token(name, "norm") || Token(name, "normal") || Suffix(name, 'n')) return TextureKind.Normal;
        if (Token(name, "mask") || Suffix(name, 'm')) return TextureKind.Mask;
        return TextureKind.Unknown;
    }
    static bool Suffix(string value, char suffix) => value.Length >= 2 && value[^2] == '_' && char.ToLowerInvariant(value[^1]) == suffix;
    static bool Token(string value, string token)
    {
        for (var at = 0; at < value.Length; at++)
        {
            var index = value.IndexOf(token, at, StringComparison.OrdinalIgnoreCase);
            if (index < 0) return false;
            var end = index + token.Length;
            if ((index == 0 || !char.IsLetterOrDigit(value[index - 1])) && (end == value.Length || !char.IsLetterOrDigit(value[end]))) return true;
            at = index;
        }
        return false;
    }
    static (int Width, int Height) Resize(int width, int height, int max)
    {
        var sourceMax = Math.Max(width, height);
        if (sourceMax <= max) return (width, height);
        var scale = (double)max / sourceMax;
        return (Math.Max(1, (int)Math.Round(width * scale)), Math.Max(1, (int)Math.Round(height * scale)));
    }
    static TextureInfo? ReadInfo(string path) => Path.GetExtension(path).Equals(".tex", StringComparison.OrdinalIgnoreCase) ? FfxivTexBridge.ReadInfo(path) : DdsReader.Read(path);
}

static class DdsReader
{
    const uint Magic = 0x20534444, Dx10 = 0x30315844;
    public static TextureInfo? Read(string path)
    {
        var dds = ReadRaw(path);
        return dds is null ? null : new(dds.Width, dds.Height, dds.Mips, dds.Format, dds.Name, TextureContainer.Dds);
    }
    public static DdsInfo? ReadRaw(string path)
    {
        if (!File.Exists(path)) return null;
        using var stream = File.OpenRead(path); using var reader = new BinaryReader(stream);
        if (stream.Length < 128 || reader.ReadUInt32() != Magic || reader.ReadUInt32() != 124) return null;
        _ = reader.ReadUInt32(); var height = (int)reader.ReadUInt32(); var width = (int)reader.ReadUInt32();
        _ = reader.ReadUInt32(); _ = reader.ReadUInt32(); var mips = Math.Max(1, (int)reader.ReadUInt32());
        stream.Position = 84; var fourCc = reader.ReadUInt32();
        if (fourCc == Dx10 && stream.Length >= 148)
        {
            stream.Position = 128; var dxgi = reader.ReadUInt32();
            return new(width, height, mips, Map(dxgi), $"DXGI_{dxgi}", 148, dxgi);
        }
        stream.Position = 80; var flags = reader.ReadUInt32(); _ = reader.ReadUInt32(); var bits = reader.ReadUInt32();
        var r = reader.ReadUInt32(); var g = reader.ReadUInt32(); var b = reader.ReadUInt32(); var a = reader.ReadUInt32();
        if ((flags & 0x40) != 0 && bits == 32 && r == 0x00ff0000 && g == 0x0000ff00 && b == 0x000000ff && a == 0xff000000)
            return new(width, height, mips, DdsFormat.Bgra, "B8G8R8A8_UNORM", 128, 87);
        return new(width, height, mips, DdsFormat.Unknown, "Legacy DDS", 128, 0);
    }
    public static DdsFormat Map(uint dxgi) => dxgi switch { 71 or 72 => DdsFormat.Bc1, 77 or 78 => DdsFormat.Bc3, 83 or 84 => DdsFormat.Bc5, 87 or 91 => DdsFormat.Bgra, 98 or 99 => DdsFormat.Bc7, _ => DdsFormat.Unknown };
}

static class FfxivTexBridge
{
    const int HeaderSize = 0x50;
    const uint Bc1 = 0x3420, Bc3 = 0x3431, Bc5 = 0x6230, Bgra = 0x1450, Bc7 = 0x6432;
    public static TextureInfo? ReadInfo(string path)
    {
        var h = ReadHeader(path); if (h is null) return null;
        var format = h.Value.Format switch { Bc1 => DdsFormat.Bc1, Bc3 => DdsFormat.Bc3, Bc5 => DdsFormat.Bc5, Bgra => DdsFormat.Bgra, Bc7 => DdsFormat.Bc7, _ => DdsFormat.Unknown };
        return new(h.Value.Width, h.Value.Height, h.Value.Mips, format, $"TEX_0x{h.Value.Format:X4}", TextureContainer.FfxivTex);
    }
    public static bool TryWriteDds(string tex, string dds, out string error)
    {
        var h = ReadHeader(tex); if (h is null) { error = "FFXIV .tex header could not be read."; return false; }
        var dxgi = h.Value.Format switch { Bc1 => 71u, Bc3 => 77u, Bc5 => 83u, Bgra => 87u, Bc7 => 98u, _ => 0u };
        if (dxgi == 0) { error = $"Unsupported FFXIV .tex format 0x{h.Value.Format:X8}."; return false; }
        var bytes = File.ReadAllBytes(tex);
        using var stream = File.Create(dds); using var w = new BinaryWriter(stream);
        WriteDdsHeader(w, h.Value.Width, h.Value.Height, h.Value.Mips, dxgi, MipSize(h.Value.Width, h.Value.Height, DdsReader.Map(dxgi)));
        w.Write(bytes, HeaderSize, bytes.Length - HeaderSize); error = ""; return true;
    }
    public static bool TryWriteTex(string dds, string tex, out string error)
    {
        var info = DdsReader.ReadRaw(dds); if (info is null) { error = "Converted DDS header could not be read."; return false; }
        var format = info.Dxgi switch { 71 or 72 => Bc1, 83 or 84 => Bc5, 87 or 91 => Bgra, 98 or 99 => Bc7, _ => 0u };
        if (format == 0) { error = $"Unsupported converted DDS format DXGI_{info.Dxgi}."; return false; }
        var bytes = File.ReadAllBytes(dds); if (info.DataOffset >= bytes.Length) { error = "Converted DDS has no payload."; return false; }
        using var stream = File.Create(tex); using var w = new BinaryWriter(stream);
        w.Write(0x00800000u); w.Write(format); w.Write((ushort)info.Width); w.Write((ushort)info.Height); w.Write((ushort)1); w.Write((ushort)Math.Min(13, info.Mips)); w.Write(0); w.Write(0); w.Write(0);
        var offset = HeaderSize;
        for (var i = 0; i < 13; i++) { if (i < info.Mips) { w.Write(offset); offset += MipSize(Math.Max(1, info.Width >> i), Math.Max(1, info.Height >> i), info.Format); } else w.Write(0); }
        w.Write(bytes, info.DataOffset, bytes.Length - info.DataOffset); error = ""; return true;
    }
    static (uint Format, int Width, int Height, int Mips)? ReadHeader(string path)
    {
        if (!File.Exists(path) || new FileInfo(path).Length < HeaderSize) return null;
        using var r = new BinaryReader(File.OpenRead(path)); _ = r.ReadUInt32(); var format = r.ReadUInt32(); var width = r.ReadUInt16(); var height = r.ReadUInt16(); _ = r.ReadUInt16(); var mips = r.ReadUInt16();
        return width == 0 || height == 0 || mips is 0 or > 13 ? null : (format, width, height, mips);
    }
    static void WriteDdsHeader(BinaryWriter w, int width, int height, int mips, uint dxgi, int topMip)
    {
        w.Write(0x20534444u); w.Write(124u); w.Write(0x000A1007u); w.Write((uint)height); w.Write((uint)width); w.Write((uint)topMip); w.Write(0u); w.Write((uint)mips);
        for (var i = 0; i < 11; i++) w.Write(0u);
        w.Write(32u); w.Write(4u); w.Write(0x30315844u); for (var i = 0; i < 5; i++) w.Write(0u);
        w.Write(mips > 1 ? 0x401008u : 0x1000u); for (var i = 0; i < 4; i++) w.Write(0u);
        w.Write(dxgi); w.Write(3u); w.Write(0u); w.Write(1u); w.Write(0u);
    }
    static int MipSize(int width, int height, DdsFormat format) => format switch
    {
        DdsFormat.Bc1 => Math.Max(1, (width + 3) / 4) * Math.Max(1, (height + 3) / 4) * 8,
        DdsFormat.Bc3 or DdsFormat.Bc5 or DdsFormat.Bc7 => Math.Max(1, (width + 3) / 4) * Math.Max(1, (height + 3) / 4) * 16,
        DdsFormat.Bgra => width * height * 4, _ => 0
    };
}

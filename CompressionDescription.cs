using System.Globalization;

namespace PenumbraTextureWatcher;

static class CompressionDescription
{
    const string CurrentMarker = "Compressed by AetherPress";
    const string LegacyMarker = "Optimized by AetherPress";

    internal static bool ContainsTag(string description) =>
        description.Contains(CurrentMarker, StringComparison.OrdinalIgnoreCase)
        || description.Contains(LegacyMarker, StringComparison.OrdinalIgnoreCase);

    internal static string AppendOrUpdate(string description, long bytesSaved)
    {
        var lines = description.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n').ToList();
        lines.RemoveAll(line => ContainsTag(line));
        while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[^1])) lines.RemoveAt(lines.Count - 1);

        var existing = string.Join(Environment.NewLine, lines).TrimEnd();
        var note = $"{CurrentMarker} — Saved {FormatMegabytes(bytesSaved)} MB";
        return string.IsNullOrWhiteSpace(existing)
            ? note
            : existing + Environment.NewLine + Environment.NewLine + note;
    }

    internal static string FormatMegabytes(long bytes) =>
        (Math.Max(0, bytes) / (1024d * 1024d)).ToString("0.00", CultureInfo.InvariantCulture);
}

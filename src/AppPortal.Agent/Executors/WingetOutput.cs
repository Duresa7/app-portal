using System.Globalization;
using System.Text.RegularExpressions;

namespace AppPortal.Agent.Executors;

/// <summary>
/// Turning what winget prints into something a person watching a progress bar can use. It writes a
/// redrawn bar rather than structured output, so this reads a percentage where one is on the line and
/// falls back to naming the phase, which is still better than a card that says nothing for six minutes.
/// </summary>
public static partial class WingetOutput
{
    [GeneratedRegex(@"(\d{1,3}(?:[.,]\d+)?)\s*%", RegexOptions.CultureInvariant)]
    private static partial Regex Percentage { get; }

    /// <summary>What one line of output means, or null when it says nothing worth reporting.</summary>
    public static (int? Percent, string? Detail) Read(string line)
    {
        var text = line.Trim();
        if (text.Length == 0)
        {
            return (null, null);
        }

        var phase = Phase(text);
        var match = Percentage.Match(text);
        if (!match.Success)
        {
            return (null, phase);
        }

        var number = match.Groups[1].Value.Replace(',', '.');
        if (!double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out var percent))
        {
            return (null, phase);
        }

        var rounded = (int)Math.Clamp(Math.Round(percent), 0, 100);
        return (rounded, $"{phase ?? "Downloading"} {rounded}%");
    }

    private static string? Phase(string text)
    {
        if (text.Contains("Download", StringComparison.OrdinalIgnoreCase))
        {
            return "Downloading";
        }

        if (text.Contains("Verify", StringComparison.OrdinalIgnoreCase)
            || text.Contains("hash", StringComparison.OrdinalIgnoreCase))
        {
            return "Verifying download";
        }

        if (text.Contains("Install", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Starting package install", StringComparison.OrdinalIgnoreCase))
        {
            return "Installing";
        }

        return null;
    }

    /// <summary>
    /// The end of the output, for the detail on a failure. The interesting part of a failed run is the
    /// last thing it said, and a whole transcript does not belong in a database column or on a card.
    /// </summary>
    public static string Tail(string output, int bytes = 4096)
    {
        var text = output.TrimEnd();
        if (text.Length <= bytes)
        {
            return text;
        }

        var cut = text[^bytes..];
        var newline = cut.IndexOf('\n');
        return newline >= 0 && newline < cut.Length - 1 ? cut[(newline + 1)..] : cut;
    }
}

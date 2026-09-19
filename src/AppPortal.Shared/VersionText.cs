using System;

namespace AppPortal.Shared;

/// <summary>
/// Version strings as they arrive: a release tag ("v0.2.0"), a file version ("0.2.0.0"), an
/// informational version ("0.2.0+abc123"). All of them compare as four-part versions, because
/// System.Version treats a missing component as -1 and would otherwise call 0.2.0 older than 0.2.0.0.
/// </summary>
public static class VersionText
{
    public static bool TryParse(string? text, out Version version)
    {
        version = new Version(0, 0, 0, 0);
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var s = text.Trim();
        if (s.StartsWith('v') || s.StartsWith('V'))
        {
            s = s[1..];
        }

        var cut = s.IndexOfAny(['+', '-', ' ']);
        if (cut >= 0)
        {
            s = s[..cut];
        }

        if (!s.Contains('.'))
        {
            s += ".0";
        }

        if (!Version.TryParse(s, out var parsed) || parsed is null)
        {
            return false;
        }

        version = new Version(parsed.Major, parsed.Minor, Math.Max(parsed.Build, 0), Math.Max(parsed.Revision, 0));
        return true;
    }

    /// <summary>"0.2.0", the form shown to people and used on tags.</summary>
    public static string Short(Version version) => version.ToString(3);

    public static bool IsNewer(string? candidate, string? current)
        => TryParse(candidate, out var c) && TryParse(current, out var r) && c > r;
}

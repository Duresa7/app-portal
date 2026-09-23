using System.Globalization;
using System.Text;

namespace AppPortal.Shared;

/// <summary>
/// The name and id a new catalog app is offered when an administrator creates it from a request. One
/// function for the web form and the client editor, so both suggest the same thing from the same text.
/// Only the name and id are guessed: anything else in a free-text request is left for a person to read.
/// </summary>
public static class RequestSuggestion
{
    public const int MaxNameLength = 80;

    public const int MaxIdLength = 64;

    private static readonly char[] Stops = [',', ';', ':', '(', '!', '?'];

    /// <summary>
    /// The start of the request, up to the first thing that reads like the end of the app's name:
    /// "Slack, for the new support rota" suggests "Slack". A dot only ends it before a space, so
    /// "Node.js" survives.
    /// </summary>
    public static string Name(string? requestText)
    {
        var text = requestText ?? "";
        var lineEnd = text.IndexOfAny(['\r', '\n']);
        var line = lineEnd >= 0 ? text[..lineEnd] : text;

        var cut = line.Length;
        var stop = line.IndexOfAny(Stops);
        if (stop >= 0)
        {
            cut = stop;
        }

        var dash = line.IndexOf(" - ", StringComparison.Ordinal);
        if (dash >= 0 && dash < cut)
        {
            cut = dash;
        }

        for (var i = 0; i < cut; i++)
        {
            if (line[i] == '.' && (i + 1 == line.Length || char.IsWhiteSpace(line[i + 1])))
            {
                cut = i;
                break;
            }
        }

        var name = line[..cut].Trim();
        if (name.Length > MaxNameLength)
        {
            var space = name.LastIndexOf(' ', MaxNameLength);
            name = (space > 0 ? name[..space] : name[..MaxNameLength]).TrimEnd();
        }

        return name;
    }

    /// <summary>
    /// A catalog id made from a name: lower-case ASCII letters and digits with single dashes between
    /// them. Empty when nothing usable is left, or when the result is a word the catalog pages reserve.
    /// </summary>
    public static string Id(string? name)
    {
        var decomposed = (name ?? "").Normalize(NormalizationForm.FormD);
        var id = new StringBuilder(decomposed.Length);
        var dash = false;
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            var lower = char.ToLowerInvariant(c);
            if (char.IsAsciiLetterOrDigit(lower))
            {
                id.Append(lower);
                dash = false;
            }
            else if (!dash)
            {
                id.Append('-');
                dash = true;
            }
        }

        var result = id.ToString().Trim('-');
        if (result.Length > MaxIdLength)
        {
            result = result[..MaxIdLength].Trim('-');
        }

        return result is "new" or "export" ? "" : result;
    }
}

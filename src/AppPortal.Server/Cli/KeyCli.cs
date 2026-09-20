using System.Globalization;

using AppPortal.Server.Enrollment;

namespace AppPortal.Server.Cli;

/// <summary>
/// `key create|list|revoke`, for setups that are scripted rather than clicked. The plaintext is printed
/// exactly once, the same as the admin page shows it once; nothing stores it afterwards.
/// </summary>
public static class KeyCli
{
    public static int Run(string[] args, EnrollmentKeyStore keys, TextWriter output)
    {
        if (args.Length < 2 || args[0] != "key")
        {
            return Usage(output);
        }

        switch (args[1])
        {
            case "create":
                {
                    var name = Option(args, "--name");
                    if (name is null)
                    {
                        return Usage(output);
                    }

                    try
                    {
                        var created = keys.Create(
                            name,
                            EnrollmentKeyStore.ParseEngine(Option(args, "--engine") ?? "action1"),
                            ParseExpiry(Option(args, "--expires")),
                            ParseMaxUses(Option(args, "--max-uses")),
                            "cli");

                        output.WriteLine($"Enrollment key '{created.Key.Name}' created.");
                        output.WriteLine("Key (shown once):");
                        output.WriteLine(created.Plaintext);
                        return 0;
                    }
                    catch (EnrollmentKeyRejectedException ex)
                    {
                        output.WriteLine(ex.Message);
                        return 1;
                    }
                }

            case "list":
                foreach (var key in keys.List())
                {
                    output.WriteLine($"{key.Name}\t{key.DisplayPrefix}...\t{EnrollmentKeyStore.Name(key.DefaultEngine)}\t{key.UsesText}\t{key.Status.ToString().ToLowerInvariant()}\t{key.CreatedAt:u}");
                }

                return 0;

            case "revoke":
                {
                    var id = Option(args, "--id");
                    if (id is null)
                    {
                        return Usage(output);
                    }

                    output.WriteLine(keys.Revoke(id) ? $"Revoked {id}." : $"No active key with id {id}.");
                    return 0;
                }

            default:
                return Usage(output);
        }
    }

    private static DateTimeOffset? ParseExpiry(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        // A bare date is good until the end of that day, so --expires today does not mean "already gone".
        if (DateTime.TryParseExact(text.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            return new DateTimeOffset(date.Date.AddDays(1).AddTicks(-1), TimeSpan.Zero);
        }

        return DateTimeOffset.TryParse(text.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
            ? parsed
            : throw new EnrollmentKeyRejectedException($"'{text}' is not a date. Write it as YYYY-MM-DD.");
    }

    private static int? ParseMaxUses(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        return int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var max)
            ? max
            : throw new EnrollmentKeyRejectedException($"'{text}' is not a whole number of uses.");
    }

    private static string? Option(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static int Usage(TextWriter output)
    {
        output.WriteLine("Usage:");
        output.WriteLine("  AppPortal.Server key create --name <name> [--expires <YYYY-MM-DD>] [--max-uses <n>] [--engine action1|agent|both]");
        output.WriteLine("  AppPortal.Server key list");
        output.WriteLine("  AppPortal.Server key revoke --id <key-id>");
        return 2;
    }
}

using System;
using System.Collections.Generic;

namespace AppPortal.Setup.Services;

/// <summary>
/// The command line, parsed. Nothing here touches the network or the disk, so every rule an RMM can
/// trip over is decided in one place and proved by a test rather than on a virtual machine.
/// </summary>
public sealed record SetupArguments(
    bool Quiet,
    bool Help,
    string? ServerUrl,
    string? EnrollmentKey,
    string? Action1EndpointId)
{
    public const string Usage = """
        AppPortalSetup.exe                  Run the wizard.
        AppPortalSetup.exe /quiet /server <url> /key <key> [/endpoint <id>]

          /quiet               Install without a window. Needs /server and /key.
          /server <url>        The App Portal server, for example https://portal.example.internal
          /key <key>           An enrollment key from the server's Keys page.
          /endpoint <id>       The Action1 endpoint id, when the key enrolls for Action1.
          /?                   This text.

        Exit codes: 0 installed and enrolled, 1 enrollment failed, 2 the command line was wrong,
        3010 installed and the PC has to restart, anything else the code msiexec returned.
        """;

    /// <summary>
    /// Characters the MSI refuses in <c>SERVERURL</c>, <c>ENROLLMENTKEY</c> and <c>ACTION1ENDPOINTID</c>
    /// because they would change the JSON it writes. Refusing them here means the tech is told before
    /// anything is installed, and it keeps a value from breaking the msiexec command line as well.
    /// </summary>
    private static readonly char[] Forbidden = ['"', '\\', '\t', '\r', '\n'];

    /// <summary>
    /// Reads the command line. The result carries either the arguments or the one thing wrong with them;
    /// a caller that gets an error exits with <see cref="ExitCodes.InvalidArguments"/>.
    /// </summary>
    public static SetupArgumentResult Parse(IReadOnlyList<string> args)
    {
        var quiet = false;
        var help = false;
        string? server = null;
        string? key = null;
        string? endpoint = null;

        for (var i = 0; i < args.Count; i++)
        {
            var name = Switch(args[i]);
            switch (name)
            {
                case "quiet" or "q" or "silent":
                    quiet = true;
                    break;
                case "?" or "h" or "help":
                    help = true;
                    break;
                case "server" or "key" or "endpoint":
                    if (i + 1 >= args.Count || Switch(args[i + 1]) is not null)
                    {
                        return Bad($"/{name} needs a value after it.");
                    }

                    var value = args[++i];
                    switch (name)
                    {
                        case "server": server = value; break;
                        case "key": key = value; break;
                        default: endpoint = value; break;
                    }

                    break;
                case null:
                    return Bad($"'{args[i]}' is not one of this installer's switches.");
                default:
                    return Bad($"/{name} is not one of this installer's switches.");
            }
        }

        if (Problem(server, "The server URL") is { } serverProblem)
        {
            return Bad(serverProblem);
        }

        if (Problem(key, "The enrollment key") is { } keyProblem)
        {
            return Bad(keyProblem);
        }

        if (Problem(endpoint, "The Action1 endpoint id") is { } endpointProblem)
        {
            return Bad(endpointProblem);
        }

        if (server is not null && ServerUrlProblem(server) is { } shape)
        {
            return Bad(shape);
        }

        // Without a window there is nobody to ask, so the two things the installer cannot invent have
        // to be on the command line. With a window they are only a head start on the second page.
        if (quiet && (string.IsNullOrWhiteSpace(server) || string.IsNullOrWhiteSpace(key)))
        {
            return Bad("/quiet needs both /server and /key.");
        }

        return new SetupArgumentResult(
            new SetupArguments(quiet, help, Trimmed(server), Trimmed(key), Trimmed(endpoint)),
            null);
    }

    /// <summary>
    /// What is wrong with a server URL, or null when nothing is. The same rule the agent applies before
    /// it enrolls, applied early enough that the tech can correct a typo instead of reading a log.
    /// </summary>
    public static string? ServerUrlProblem(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return "Enter the address of your App Portal server.";
        }

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var address)
            || (address.Scheme != Uri.UriSchemeHttps && address.Scheme != Uri.UriSchemeHttp))
        {
            return "The server address has to start with https:// or http://.";
        }

        if (!string.IsNullOrEmpty(address.UserInfo) || !string.IsNullOrEmpty(address.Query) || !string.IsNullOrEmpty(address.Fragment))
        {
            return "Give the server's address on its own, without a sign-in, a query or a fragment.";
        }

        return null;
    }

    /// <summary>The value as the MSI will receive it, trimmed of the spaces a paste tends to carry.</summary>
    public static string? Trimmed(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? Problem(string? value, string what)
        => value is not null && value.IndexOfAny(Forbidden) >= 0
            ? $"{what} must not contain quotes, backslashes, tabs or line breaks."
            : null;

    /// <summary>The name behind <c>/x</c>, <c>-x</c> or <c>--x</c>, lower-cased, or null for a bare word.</summary>
    private static string? Switch(string argument)
    {
        if (argument.StartsWith("--", StringComparison.Ordinal))
        {
            return argument[2..].ToLowerInvariant();
        }

        return argument.Length > 1 && argument[0] is '/' or '-'
            ? argument[1..].ToLowerInvariant()
            : null;
    }

    private static SetupArgumentResult Bad(string message) => new(null, message);
}

/// <summary>Either the arguments or the reason the command line was refused; never both, never neither.</summary>
public sealed record SetupArgumentResult(SetupArguments? Arguments, string? Error);

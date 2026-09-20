using AppPortal.Server.Admin;

namespace AppPortal.Server.Cli;

/// <summary>
/// `admin add|list|reset-password|disable`, run against the same database the server reads. This is how
/// the first administrator comes into being: there is no default account and no bootstrap password.
/// </summary>
public static class AdminCli
{
    /// <summary>
    /// Lets a scripted install supply the password without it reaching the terminal or the shell history.
    /// Prompting is the interactive path.
    /// </summary>
    public const string PasswordVariable = "APPPORTAL_ADMIN_PASSWORD";

    public static int Run(string[] args, AdminStore admins, AdminSessionStore sessions, TextWriter output, Func<string?>? readPassword = null)
    {
        if (args.Length < 2 || args[0] != "admin")
        {
            return Usage(output);
        }

        try
        {
            switch (args[1])
            {
                case "add":
                    {
                        var username = Option(args, "--username");
                        if (username is null)
                        {
                            return Usage(output);
                        }

                        var password = Password(output, readPassword);
                        if (password is null)
                        {
                            return 1;
                        }

                        admins.Add(username, password);
                        output.WriteLine($"Administrator '{username}' created. Sign in at /admin/login.");
                        return 0;
                    }

                case "list":
                    foreach (var admin in admins.All())
                    {
                        var lastLogin = admin.LastLoginAt is { } at ? $"{at:u}" : "never signed in";
                        output.WriteLine($"{admin.Username}\t{admin.Source}\t{(admin.Disabled ? "disabled" : "enabled")}\t{lastLogin}");
                    }

                    return 0;

                case "reset-password":
                    {
                        var username = Option(args, "--username");
                        if (username is null)
                        {
                            return Usage(output);
                        }

                        var password = Password(output, readPassword);
                        if (password is null)
                        {
                            return 1;
                        }

                        admins.SetPassword(username, password);

                        // Whoever knew the old password may be the reason it is being reset.
                        var record = admins.Find(username);
                        if (record is not null)
                        {
                            sessions.RevokeAllFor(record.Id);
                        }

                        output.WriteLine($"Password changed for '{username}'. Every session it had is signed out.");
                        return 0;
                    }

                case "disable":
                    {
                        var username = Option(args, "--username");
                        if (username is null)
                        {
                            return Usage(output);
                        }

                        admins.SetDisabled(username, true);
                        var record = admins.Find(username);
                        if (record is not null)
                        {
                            sessions.RevokeAllFor(record.Id);
                        }

                        output.WriteLine($"Administrator '{username}' is disabled and signed out.");
                        return 0;
                    }

                default:
                    return Usage(output);
            }
        }
        catch (AdminRejectedException ex)
        {
            output.WriteLine(ex.Message);
            return 1;
        }
    }

    private static string? Password(TextWriter output, Func<string?>? readPassword)
    {
        var fromEnvironment = Environment.GetEnvironmentVariable(PasswordVariable);
        if (!string.IsNullOrEmpty(fromEnvironment))
        {
            return fromEnvironment;
        }

        var password = (readPassword ?? ReadFromConsole)();
        if (string.IsNullOrEmpty(password))
        {
            output.WriteLine($"No password given. Type one when prompted, or set {PasswordVariable} for an unattended install.");
            return null;
        }

        return password;
    }

    private static string? ReadFromConsole()
    {
        Console.Write("Password: ");
        var password = ReadHidden();
        Console.WriteLine();
        Console.Write("Repeat it: ");
        var again = ReadHidden();
        Console.WriteLine();
        if (password != again)
        {
            Console.WriteLine("The two did not match.");
            return null;
        }

        return password;
    }

    /// <summary>Reads without echoing. Falls back to a plain read where there is no console to control.</summary>
    private static string ReadHidden()
    {
        if (Console.IsInputRedirected)
        {
            return Console.ReadLine() ?? "";
        }

        var typed = new System.Text.StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                return typed.ToString();
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (typed.Length > 0)
                {
                    typed.Length--;
                }

                continue;
            }

            if (!char.IsControl(key.KeyChar))
            {
                typed.Append(key.KeyChar);
            }
        }
    }

    private static string? Option(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static int Usage(TextWriter output)
    {
        output.WriteLine("Usage:");
        output.WriteLine("  AppPortal.Server admin add --username <name>");
        output.WriteLine("  AppPortal.Server admin list");
        output.WriteLine("  AppPortal.Server admin reset-password --username <name>");
        output.WriteLine("  AppPortal.Server admin disable --username <name>");
        output.WriteLine($"The password is read from {PasswordVariable} when it is set, and prompted for otherwise.");
        return 2;
    }
}

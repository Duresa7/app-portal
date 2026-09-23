// The installer the real-PC proof installs. Test-RealPc.ps1 compiles it on the PC under test with the
// C# compiler that ships with Windows, so no binary is committed and nothing is downloaded:
//
//   %WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe /nologo /target:exe /platform:anycpu
//       /out:proof-installer.exe ProofInstaller.cs
//
// That compiler speaks C# 5, so this file does too: no string interpolation, no expression-bodied
// members, no null-conditional operators.
//
// It installs nothing useful. What it does is write down the facts the proof needs, which a real
// installer never reports: who it ran as, whether that token was elevated, and in which session.
//
//   install --id <id> --scope user|machine [--exit <code>] [--linger <seconds>]
//   uninstall --id <id> --scope user|machine
//   linger --seconds <n>        (what --linger starts; not for use by hand)

using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

using Microsoft.Win32;

namespace AppPortalProof
{
    internal static class Program
    {
        private const string UninstallBranch = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\";

        private const string ProductFolder = "App Portal Proof";

        private const string ExecutableName = "proof-installer.exe";

        private static int Main(string[] args)
        {
            try
            {
                if (args.Length == 0)
                {
                    return Usage();
                }

                switch (args[0])
                {
                    case "install":
                        return Install(args);
                    case "uninstall":
                        return Uninstall(args);
                    case "linger":
                        return Linger(args);
                    default:
                        return Usage();
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("proof-installer: " + ex.GetType().Name + ": " + ex.Message);
                return 1;
            }
        }

        private static int Install(string[] args)
        {
            var id = Id(args);
            var scope = Scope(args);
            var exitCode = Number(args, "--exit", 0);
            var linger = Number(args, "--linger", 0);
            if (id == null || scope == null || linger < 0)
            {
                return Usage();
            }

            var directory = InstallDirectory(scope, id);
            Directory.CreateDirectory(directory);
            var copy = Path.Combine(directory, ExecutableName);
            CopySelf(copy);

            string account;
            string sid;
            using (var identity = WindowsIdentity.GetCurrent())
            {
                account = identity.Name;
                sid = identity.User == null ? "" : identity.User.Value;
            }

            var elevated = IsElevated();
            var session = Process.GetCurrentProcess().SessionId;
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

            var json = new StringBuilder();
            json.Append("{\n");
            json.Append("  \"id\": ").Append(Quote(id)).Append(",\n");
            json.Append("  \"scope\": ").Append(Quote(scope)).Append(",\n");
            json.Append("  \"account\": ").Append(Quote(account)).Append(",\n");
            json.Append("  \"sid\": ").Append(Quote(sid)).Append(",\n");
            json.Append("  \"elevated\": ").Append(elevated ? "true" : "false").Append(",\n");
            json.Append("  \"sessionId\": ").Append(session.ToString(CultureInfo.InvariantCulture)).Append(",\n");
            json.Append("  \"localAppData\": ").Append(Quote(localAppData)).Append(",\n");
            json.Append("  \"path\": ").Append(Quote(copy)).Append(",\n");
            json.Append("  \"installedAt\": ").Append(Quote(DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture))).Append("\n");
            json.Append("}\n");
            File.WriteAllText(Path.Combine(directory, "installed.json"), json.ToString(), new UTF8Encoding(false));

            using (var root = RegistryKey.OpenBaseKey(Hive(scope), RegistryView.Registry64))
            using (var key = root.CreateSubKey(UninstallBranch + KeyName(id)))
            {
                var remove = "\"" + copy + "\" uninstall --id " + id + " --scope " + scope;
                key.SetValue("DisplayName", DisplayName(id));
                key.SetValue("DisplayVersion", "1.0.0");
                key.SetValue("Publisher", "App Portal");
                key.SetValue("InstallLocation", directory);
                key.SetValue("UninstallString", remove);
                key.SetValue("QuietUninstallString", remove);
                key.SetValue("NoModify", 1, RegistryValueKind.DWord);
                key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            }

            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "{0} installed for {1} scope in {2} as {3} (elevated: {4}, session {5}).",
                DisplayName(id), scope, directory, account, elevated ? "yes" : "no", session));

            if (linger > 0)
            {
                // The shape of an installer that starts its app when it finishes. The child inherits
                // every inheritable handle, the pipe the agent reads this output from included, and the
                // installer exits without waiting for it.
                var start = new ProcessStartInfo(copy, "linger --seconds " + linger.ToString(CultureInfo.InvariantCulture));
                start.UseShellExecute = false;
                start.CreateNoWindow = true;
                using (var child = Process.Start(start))
                {
                    Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                        "Started {0} to linger for {1} seconds (process {2}).", copy, linger, child.Id));
                }
            }

            return exitCode;
        }

        private static int Uninstall(string[] args)
        {
            var id = Id(args);
            var scope = Scope(args);
            if (id == null || scope == null)
            {
                return Usage();
            }

            using (var root = RegistryKey.OpenBaseKey(Hive(scope), RegistryView.Registry64))
            {
                root.DeleteSubKeyTree(UninstallBranch + KeyName(id), false);
            }

            var record = Path.Combine(InstallDirectory(scope, id), "installed.json");
            if (File.Exists(record))
            {
                File.Delete(record);
            }

            string account;
            using (var identity = WindowsIdentity.GetCurrent())
            {
                account = identity.Name;
            }

            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "{0} removed for {1} scope as {2}.", DisplayName(id), scope, account));
            return 0;
        }

        private static int Linger(string[] args)
        {
            var seconds = Number(args, "--seconds", 0);
            if (seconds <= 0)
            {
                return Usage();
            }

            Thread.Sleep(TimeSpan.FromSeconds(seconds));
            return 0;
        }

        private static int Usage()
        {
            Console.Error.WriteLine("Usage:");
            Console.Error.WriteLine("  proof-installer install --id <id> --scope user|machine [--exit <code>] [--linger <seconds>]");
            Console.Error.WriteLine("  proof-installer uninstall --id <id> --scope user|machine");
            return 2;
        }

        private static string DisplayName(string id)
        {
            return "App Portal Proof " + id;
        }

        private static string KeyName(string id)
        {
            return "AppPortalProof-" + id;
        }

        private static string InstallDirectory(string scope, string id)
        {
            var root = scope == "user"
                ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
                : Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            return Path.Combine(Path.Combine(root, ProductFolder), id);
        }

        private static RegistryHive Hive(string scope)
        {
            return scope == "user" ? RegistryHive.CurrentUser : RegistryHive.LocalMachine;
        }

        private static void CopySelf(string target)
        {
            var self = Process.GetCurrentProcess().MainModule.FileName;
            if (string.Equals(Path.GetFullPath(self), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            try
            {
                File.Copy(self, target, true);
            }
            catch (IOException)
            {
                // A copy an earlier run left lingering is still running and cannot be replaced. It is
                // the same program, so it serves as well as a fresh one.
                if (!File.Exists(target))
                {
                    throw;
                }
            }
        }

        /// <summary>The id names a folder and a registry key, so only what is safe in both is allowed.</summary>
        private static string Id(string[] args)
        {
            var id = Option(args, "--id");
            return id != null && Regex.IsMatch(id, "^[A-Za-z0-9._-]{1,64}$") ? id : null;
        }

        private static string Scope(string[] args)
        {
            var scope = Option(args, "--scope");
            return scope == "user" || scope == "machine" ? scope : null;
        }

        private static int Number(string[] args, string name, int fallback)
        {
            var text = Option(args, name);
            int value;
            if (text == null)
            {
                return fallback;
            }

            if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
            {
                throw new ArgumentException(name + " needs a whole number.");
            }

            return value;
        }

        private static string Option(string[] args, string name)
        {
            for (var i = 1; i < args.Length - 1; i++)
            {
                if (args[i] == name)
                {
                    return args[i + 1];
                }
            }

            return null;
        }

        private static string Quote(string value)
        {
            var text = new StringBuilder("\"");
            foreach (var c in value)
            {
                switch (c)
                {
                    case '"':
                        text.Append("\\\"");
                        break;
                    case '\\':
                        text.Append("\\\\");
                        break;
                    case '\n':
                        text.Append("\\n");
                        break;
                    case '\r':
                        text.Append("\\r");
                        break;
                    case '\t':
                        text.Append("\\t");
                        break;
                    default:
                        if (c < ' ')
                        {
                            text.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            text.Append(c);
                        }

                        break;
                }
            }

            return text.Append('"').ToString();
        }

        /// <summary>
        /// Whether this process's token is the elevated one. Under User Account Control an administrator
        /// runs with a filtered token, so being in the Administrators group says nothing about it.
        /// </summary>
        private static bool IsElevated()
        {
            using (var identity = WindowsIdentity.GetCurrent())
            {
                int elevated;
                int returned;
                return GetTokenInformation(identity.Token, TokenElevation, out elevated, sizeof(int), out returned) && elevated != 0;
            }
        }

        private const int TokenElevation = 20;

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetTokenInformation(IntPtr token, int informationClass, out int information,
            int length, out int returned);
    }
}

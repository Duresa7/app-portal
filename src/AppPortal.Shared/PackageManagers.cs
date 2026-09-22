using System.Text.RegularExpressions;

namespace AppPortal.Shared;

/// <summary>
/// One package manager, described rather than coded. Every one of these tools is the same shape: find
/// an executable, run it with an install verb and a package id, read the exit code, watch the output.
/// A definition record and an executor per manager would be ten copies of one code path, and each copy
/// would need its own JSON attribute, its own kind switch arm, its own admin form section and its own
/// frozen shape test. One table means adding the eleventh manager is a row.
/// </summary>
/// <param name="Name">What the catalog calls it. Lower case with a hyphen, and never changed.</param>
/// <param name="DisplayName">What an administrator reads.</param>
/// <param name="Purpose">
/// One sentence saying what this manager is for. It lives on the record rather than in documentation
/// because it is what an administrator reads while choosing, and a choice made without it is how a web
/// browser ends up being deployed through npm.
/// </param>
/// <param name="Executable">
/// The command to run. Several of these are <c>.cmd</c> files, which is why <see cref="IdRule"/> is
/// strict.
/// </param>
/// <param name="ProbeDirectories">
/// Where to look when the executable is not on the PATH the agent inherits. SYSTEM's PATH is not the
/// PATH of the person who installed the manager, and most of these installers only edit the latter.
/// A <c>%NAME%</c> in an entry is expanded before it is used.
/// </param>
/// <param name="Install">
/// The install arguments as a template. <c>{package}</c>, <c>{scope}</c>, <c>{version}</c> and
/// <c>{extra}</c> are replaced, and each may be empty. Tokens rather than appending, because the
/// PowerShell managers carry their whole command inside one quoted argument and anything appended
/// after it lands outside the quotes.
/// </param>
/// <param name="Uninstall">The uninstall arguments, with <c>{id}</c> and <c>{scope}</c>.</param>
/// <param name="List">The arguments that list what this manager has installed.</param>
/// <param name="Scopes">
/// The scopes this manager can actually carry out, in preference order, so the first is the default.
/// Several of these install into a profile whatever you ask, and one refuses to run elevated at all.
/// Saying so here is what stops an administrator finding out from a failed job on every PC at once.
/// </param>
/// <param name="PackageTemplate">
/// How the package is named on the command line, with <c>{id}</c> and optionally <c>{version}</c>.
/// When it carries <c>{version}</c>, that is how this manager pins a version.
/// </param>
/// <param name="VersionFragment">
/// How this manager pins a version when it does not do it in the package name, with <c>{version}</c>.
/// Null here and no <c>{version}</c> in <see cref="PackageTemplate"/> means it cannot pin one at all,
/// and asking for a version is refused on the admin page rather than ignored on the device.
/// </param>
/// <param name="MachineScope">What fills <c>{scope}</c> for a machine-wide install.</param>
/// <param name="UserScope">What fills <c>{scope}</c> for a per-profile install.</param>
/// <param name="AlreadyInstalled">
/// Exit codes meaning the package is already there, which is the state the caller wanted.
/// </param>
/// <param name="IdRule">
/// What a package id may contain, as an allowlist. This is the safety requirement of the whole table
/// and not a nicety. <c>IProcessRunner</c> joins arguments into one string, and <c>npm.cmd</c>,
/// <c>scoop.cmd</c> and <c>yarn.cmd</c> are batch files. Windows runs a batch file through
/// <c>cmd.exe</c>, which reads the ampersand, pipe, caret, percent and quote characters in those
/// arguments as syntax rather than as text, so an id carrying one is a command and not a package name.
/// An allowlist, never a list of characters to fear, because the list of characters to fear is the one
/// nobody finishes writing.
/// </param>
public sealed record PackageManagerDescriptor(
    string Name,
    string DisplayName,
    string Purpose,
    string Executable,
    string[] ProbeDirectories,
    string Install,
    string Uninstall,
    string List,
    string[] Scopes,
    string PackageTemplate,
    string? VersionFragment,
    string MachineScope,
    string UserScope,
    int[] AlreadyInstalled,
    Regex IdRule)
{
    /// <summary>What this manager does when the catalog does not say.</summary>
    public string DefaultScope => Scopes[0];

    /// <summary>Whether this manager can be asked for one particular version.</summary>
    public bool CanPinVersion => PackageTemplate.Contains("{version}") || VersionFragment is not null;

    /// <summary>
    /// The whole argument string for installing this package. Built here rather than in the executor so
    /// that every manager's command line is a pure function of its own row, and can be asserted
    /// character for character in a test without a process, a Windows machine, or a package that
    /// exists.
    /// </summary>
    public string InstallArguments(string id, string? version, string scope, string? extra)
    {
        var pinned = version is { Length: > 0 };
        var inName = pinned && PackageTemplate.Contains("{version}");
        return Fill(Install,
            package: inName
                ? PackageTemplate.Replace("{id}", id).Replace("{version}", version!)
                : StripVersion(PackageTemplate).Replace("{id}", id),
            scope: scope,
            version: pinned && !inName && VersionFragment is not null
                ? VersionFragment.Replace("{version}", version!)
                : "",
            extra: extra);
    }

    /// <summary>The whole argument string for taking this package off again.</summary>
    public string UninstallArguments(string id, string scope)
        => Fill(Uninstall.Replace("{id}", id), package: null, scope: scope, version: "", extra: null);

    private string Fill(string template, string? package, string scope, string version, string? extra)
    {
        var filled = template
            .Replace("{package}", package ?? "")
            .Replace("{scope}", scope == "user" ? UserScope : MachineScope)
            .Replace("{version}", version)
            .Replace("{extra}", extra?.Trim() ?? "");
        // A token that resolved to nothing leaves two spaces where it was, or one space before the
        // closing quote of a PowerShell command. Neither is what anybody would write by hand, so
        // neither is what the tests should have to expect.
        return Regex.Replace(Regex.Replace(filled, @"\s{2,}", " "), @"\s+""\z", "\"").Trim();
    }

    /// <summary>
    /// The package name with its version part taken out, for an install that asked for no version.
    /// Leaving the separator behind would ask npm for a package called <c>typescript@</c>.
    /// </summary>
    private static string StripVersion(string template)
        => Regex.Replace(template, @"[@=]*\{version\}", "");
}

/// <summary>Every package manager the agent knows, and the only place their names are written.</summary>
public static class PackageManagers
{
    /// <summary>
    /// A package id for an ecosystem whose ids are one word. Deliberately narrow: a real id has never
    /// needed anything else, and everything left out is something <c>cmd.exe</c> reads as syntax.
    /// </summary>
    private static readonly Regex Word = new(@"\A[A-Za-z0-9][A-Za-z0-9._+-]*\z", RegexOptions.Compiled);

    /// <summary>The same, plus the scope and bucket separators npm, Yarn and Scoop use.</summary>
    private static readonly Regex Scoped = new(@"\A@?[A-Za-z0-9][A-Za-z0-9._-]*(?:/[A-Za-z0-9][A-Za-z0-9._-]*)?\z", RegexOptions.Compiled);

    /// <summary>A vcpkg port, which may name features in square brackets.</summary>
    private static readonly Regex Port = new(@"\A[a-z0-9][a-z0-9-]*(?:\[[a-z0-9,-]+\])?\z", RegexOptions.Compiled);

    private const string Pwsh = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command";

    private static readonly string[] Both = ["machine", "user"];
    private static readonly string[] UserOnly = ["user"];
    private static readonly string[] MachineOnly = ["machine"];
    private static readonly string[] UserFirst = ["user", "machine"];

    public static IReadOnlyList<PackageManagerDescriptor> All { get; } =
    [
        new("choco", "Chocolatey",
            "Windows applications and tools for everyone on the PC. The closest of these to winget.",
            "choco.exe", [@"%ProgramData%\chocolatey\bin"],
            Install: "install {package} -y --no-progress --limit-output {version} {extra}",
            Uninstall: "uninstall {id} -y --limit-output",
            List: "list --limit-output",
            Scopes: MachineOnly,
            PackageTemplate: "{id}",
            VersionFragment: "--version={version}",
            MachineScope: "", UserScope: "",
            AlreadyInstalled: [],
            IdRule: Word),

        new("scoop", "Scoop",
            "Developer tools that install into one person's profile without asking for administrator rights.",
            "scoop.cmd", [@"%USERPROFILE%\scoop\shims", @"%ProgramData%\scoop\shims"],
            Install: "install {package} {scope} {extra}",
            Uninstall: "uninstall {id} {scope}",
            List: "list",
            // Scoop exists so that software can be installed without elevation, and it declines to run
            // elevated unless told to, so a profile is where it belongs and where it is tried first.
            Scopes: UserFirst,
            PackageTemplate: "{id}@{version}",
            VersionFragment: null,
            MachineScope: "--global", UserScope: "",
            AlreadyInstalled: [],
            IdRule: Scoped),

        new("npm", "npm",
            "Node.js command line tools, installed globally for the PC. Needs Node.js on the device.",
            "npm.cmd", [@"%ProgramFiles%\nodejs", @"%APPDATA%\npm"],
            Install: "install --global {package} {extra}",
            Uninstall: "uninstall --global {id}",
            List: "ls --global --depth=0",
            Scopes: Both,
            PackageTemplate: "{id}@{version}",
            VersionFragment: null,
            MachineScope: "", UserScope: "",
            AlreadyInstalled: [],
            IdRule: Scoped),

        new("yarn", "Yarn",
            "The same Node.js packages as npm, for a fleet that standardised on Yarn. Needs Node.js.",
            "yarn.cmd", [@"%ProgramFiles%\nodejs", @"%APPDATA%\npm"],
            Install: "global add {package} {extra}",
            Uninstall: "global remove {id}",
            List: "global list",
            Scopes: Both,
            PackageTemplate: "{id}@{version}",
            VersionFragment: null,
            MachineScope: "", UserScope: "",
            AlreadyInstalled: [],
            IdRule: Scoped),

        new("bun", "Bun",
            "Node.js packages through Bun, which keeps them in one person's profile. Needs Bun.",
            "bun.exe", [@"%USERPROFILE%\.bun\bin"],
            Install: "add --global {package} {extra}",
            Uninstall: "remove --global {id}",
            List: "pm ls --global",
            // Bun's global folder is under the profile and it has no machine-wide mode.
            Scopes: UserOnly,
            PackageTemplate: "{id}@{version}",
            VersionFragment: null,
            MachineScope: "", UserScope: "",
            AlreadyInstalled: [],
            IdRule: Scoped),

        new("pip", "pip",
            "Python packages and the command line tools they bring. Needs Python on the device.",
            "pip.exe", [@"%LOCALAPPDATA%\Programs\Python\Scripts", @"%ProgramFiles%\Python\Scripts"],
            Install: "install {package} {scope} --disable-pip-version-check --no-input {extra}",
            Uninstall: "uninstall {id} --yes --disable-pip-version-check",
            List: "list --disable-pip-version-check",
            Scopes: Both,
            PackageTemplate: "{id}=={version}",
            VersionFragment: null,
            MachineScope: "", UserScope: "--user",
            AlreadyInstalled: [],
            IdRule: Word),

        new("cargo", "Cargo",
            "Rust command line tools, built on the device and kept in one person's profile. Needs Rust.",
            "cargo.exe", [@"%USERPROFILE%\.cargo\bin"],
            Install: "install {package} {version} {extra}",
            Uninstall: "uninstall {id}",
            List: "install --list",
            // Cargo installs into the Rust toolchain's own folder under the profile.
            Scopes: UserOnly,
            PackageTemplate: "{id}",
            VersionFragment: "--version {version}",
            MachineScope: "", UserScope: "",
            AlreadyInstalled: [],
            IdRule: Word),

        new("vcpkg", "vcpkg",
            "C and C++ libraries into a build tree. For developer machines, not for applications.",
            "vcpkg.exe", [@"%VCPKG_ROOT%", @"C:\vcpkg"],
            Install: "install {package} {extra}",
            Uninstall: "remove {id}",
            List: "list",
            Scopes: MachineOnly,
            PackageTemplate: "{id}",
            // Classic mode takes the version the registry holds. A manifest pins versions, and a
            // manifest belongs to a repository rather than to a catalog entry.
            VersionFragment: null,
            MachineScope: "", UserScope: "",
            AlreadyInstalled: [],
            IdRule: Port),

        new("dotnet-tool", ".NET tool",
            "Command line tools published to NuGet. They install into one person's profile.",
            "dotnet.exe", [@"%ProgramFiles%\dotnet", @"%USERPROFILE%\.dotnet"],
            Install: "tool install --global {package} {version} {extra}",
            Uninstall: "tool uninstall --global {id}",
            List: "tool list --global",
            // --global means the profile's own tools folder, not the machine's.
            Scopes: UserOnly,
            PackageTemplate: "{id}",
            VersionFragment: "--version {version}",
            MachineScope: "", UserScope: "",
            AlreadyInstalled: [],
            IdRule: Word),

        new("powershell-module", "PowerShell module",
            "A module from the PowerShell Gallery, for PowerShell 7. Needs PowerShell 7 on the device.",
            "pwsh.exe", [@"%ProgramFiles%\PowerShell\7"],
            Install: Pwsh + " \"Install-Module -Name {package} -Force -AcceptLicense {scope} {version} {extra}\"",
            Uninstall: Pwsh + " \"Uninstall-Module -Name {id} -Force\"",
            List: Pwsh + " \"Get-InstalledModule\"",
            Scopes: Both,
            PackageTemplate: "{id}",
            VersionFragment: "-RequiredVersion {version}",
            MachineScope: "-Scope AllUsers", UserScope: "-Scope CurrentUser",
            AlreadyInstalled: [],
            IdRule: Word),

        new("powershell5-module", "Windows PowerShell module",
            "The same, for the Windows PowerShell 5.1 that every Windows PC already has.",
            "powershell.exe", [@"%SystemRoot%\System32\WindowsPowerShell\v1.0"],
            // Windows PowerShell 5.1 ships PowerShellGet 1.0.0.1, which has no -AcceptLicense and stops
            // on its first Install-Module to ask whether it may fetch the NuGet provider. Nobody answers
            // a question asked of a service, so the provider is fetched first, and TLS 1.2 is switched
            // on because 5.1 does not offer it by default and the Gallery refuses anything older.
            Install: Pwsh + " \"[Net.ServicePointManager]::SecurityProtocol = 'Tls12'; "
                     + "Install-PackageProvider -Name NuGet -MinimumVersion 2.8.5.201 -Force | Out-Null; "
                     + "Install-Module -Name {package} -Force {scope} {version} {extra}\"",
            Uninstall: Pwsh + " \"Uninstall-Module -Name {id} -Force\"",
            List: Pwsh + " \"Get-InstalledModule\"",
            Scopes: Both,
            PackageTemplate: "{id}",
            VersionFragment: "-RequiredVersion {version}",
            MachineScope: "-Scope AllUsers", UserScope: "-Scope CurrentUser",
            AlreadyInstalled: [],
            IdRule: Word),
    ];

    /// <summary>The manager of this name, or null when this build has never heard of it.</summary>
    public static PackageManagerDescriptor? Find(string? name)
        => name is null ? null : All.FirstOrDefault(m => m.Name == name);

    /// <summary>Their names, for a message that has to say what it would have accepted.</summary>
    public static string Names => string.Join(", ", All.Select(m => m.Name));
}

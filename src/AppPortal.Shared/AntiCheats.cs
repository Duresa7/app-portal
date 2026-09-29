namespace AppPortal.Shared;

/// <summary>An anti-cheat product and the Windows services and drivers that belong to it.</summary>
public sealed record AntiCheatProduct(string Name, IReadOnlyList<string> Services);

/// <summary>
/// One anti-cheat service or driver the agent found on its device, and what state Windows says it is
/// in. Support information for an administrator: the portal never starts, stops or changes one, and
/// never refuses an install because of what it finds.
/// </summary>
/// <param name="Type"><c>service</c> or <c>driver</c>.</param>
/// <param name="State">What the service manager reports, in lower case: <c>running</c>, <c>stopped</c>, and so on.</param>
/// <param name="StartType"><c>automatic</c>, <c>manual</c>, <c>disabled</c>, <c>boot</c> or <c>system</c>.</param>
public sealed record DeviceAntiCheat(string Product, string Service, string Type, string State, string StartType);

/// <summary>
/// The kernel anti-cheat that competitive games install, by the service names Windows lists them under.
/// Most of them have no uninstall entry of their own, so winget's list never shows them, and a stopped
/// or half-installed one is the usual reason a game refuses to start.
/// </summary>
public static class AntiCheats
{
    public static IReadOnlyList<AntiCheatProduct> All { get; } =
    [
        new("Riot Vanguard", ["vgc", "vgk"]),
        new("Easy Anti-Cheat", ["EasyAntiCheat", "EasyAntiCheat_EOS"]),
        new("BattlEye", ["BEService", "BEDaisy"]),
        new("FACEIT Anti-Cheat", ["FACEIT", "FACEITService"]),
        new("EA Javelin Anticheat", ["EAAntiCheatService"]),
        new("PunkBuster", ["PnkBstrA", "PnkBstrB"]),
        new("nProtect GameGuard", ["npggsvc"]),
        new("XIGNCODE3", ["xhunter1"]),
        new("HoYoverse anti-cheat", ["mhyprot2", "mhyprot3"]),
    ];

    /// <summary>The product a service belongs to, or null for one that is not an anti-cheat this build knows.</summary>
    public static AntiCheatProduct? Find(string? service)
        => All.FirstOrDefault(p => p.Services.Contains(service ?? "", StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// A row worth a second look: stopped when it should start on its own, or turned off. Many of these
    /// are on-demand services that are stopped between games, so a stopped manual one is ordinary.
    /// </summary>
    public static bool NeedsAttention(DeviceAntiCheat found)
        => found.StartType == "disabled" || (found.State == "stopped" && found.StartType is "automatic" or "boot" or "system");
}

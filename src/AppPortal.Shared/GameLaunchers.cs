using System.Text.RegularExpressions;

namespace AppPortal.Shared;

/// <summary>
/// A game launcher the agent can hand a game to. Each one registers a URI scheme when it is installed,
/// and opening one of its URIs in a person's session shows the launcher's own install page for that
/// game, where the person finishes with their own account.
/// </summary>
/// <param name="Name">The value a catalog definition stores.</param>
/// <param name="Scheme">The URI scheme the launcher registers, which is how the agent tells whether it is there.</param>
/// <param name="InstallUri">What to open to install a game. <c>{id}</c> is replaced with the game's id.</param>
/// <param name="UninstallUri">What to open to remove one, or null when the launcher documents no such link.</param>
/// <param name="RepairUri">What to open to have the launcher check a game's files, or null.</param>
/// <param name="IdRule">What a game id may look like. It reaches a command line, so it is an allowlist.</param>
/// <param name="WingetId">The launcher itself in winget, for an app that has to install it first.</param>
/// <param name="IdHint">Where an administrator finds a game's id, in one sentence.</param>
public sealed record GameLauncher(
    string Name,
    string DisplayName,
    string Scheme,
    string InstallUri,
    string? UninstallUri,
    string? RepairUri,
    Regex IdRule,
    string WingetId,
    string IdHint)
{
    /// <summary>The link for one game, from one of this launcher's templates.</summary>
    public string Uri(string template, string gameId) => template.Replace("{id}", gameId, StringComparison.Ordinal);
}

/// <summary>
/// The launchers a game can be handed to, one row each, the way <see cref="PackageManagers"/> holds the
/// package managers. Battle.net and the EA app are not here: neither documents a link that opens a
/// game's install, so they are offered as launchers to install and nothing more.
/// </summary>
public static class GameLaunchers
{
    private static readonly Regex Number = new(@"\A[0-9]{1,12}\z");

    /// <summary>
    /// An Epic app name such as <c>Fortnite</c>, or the namespace, catalog item and artifact ids that
    /// Epic's own shortcuts use, joined by colons.
    /// </summary>
    private static readonly Regex EpicApp = new(@"\A[A-Za-z0-9][A-Za-z0-9._:-]{0,191}\z");

    public static IReadOnlyList<GameLauncher> All { get; } =
    [
        new("steam", "Steam", "steam",
            "steam://install/{id}", "steam://uninstall/{id}", "steam://validate/{id}",
            Number, "Valve.Steam",
            "The number in the game's Steam store address, such as 730 for Counter-Strike 2."),
        new("epic", "Epic Games Launcher", "com.epicgames.launcher",
            "com.epicgames.launcher://apps/{id}?action=launch", null, null,
            EpicApp, "EpicGames.EpicGamesLauncher",
            "The app name the launcher uses for the game, such as Fortnite."),
        new("gog", "GOG Galaxy", "goggalaxy",
            "goggalaxy://openGameView/{id}", null, null,
            Number, "GOG.Galaxy",
            "The game's product id on GOG, the number in its gogdb.org address."),
        new("ubisoft", "Ubisoft Connect", "uplay",
            "uplay://launch/{id}/0", null, null,
            Number, "Ubisoft.Connect",
            "The game's Ubisoft Connect id, the number Ubisoft Connect's own shortcuts use."),
    ];

    /// <summary>Every launcher's name, for a message that has to say which it expected.</summary>
    public static string Names => string.Join(", ", All.Select(l => l.Name));

    public static GameLauncher? Find(string? name)
        => All.FirstOrDefault(l => string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase));
}

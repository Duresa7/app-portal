using System;

namespace AppPortal.Shared;

/// <summary>
/// What the updater last did, written to %ProgramData%\AppPortal\update.json for the client to read.
/// The client never talks to the release feed itself; it reads this file and asks the updater to run.
/// </summary>
public sealed record UpdateStatus(
    DateTimeOffset CheckedAt,
    string InstalledVersion,
    string? LatestVersion,
    string? StagedVersion,
    UpdateResult Result,
    string? Message);

public enum UpdateResult
{
    /// <summary>The installed build is the newest release.</summary>
    UpToDate,

    /// <summary>A newer release exists; a check-only run saw it and downloaded nothing.</summary>
    Available,

    /// <summary>A newer build is downloaded, verified and waiting for the client to close.</summary>
    Staged,

    /// <summary>A newer build replaced the installed one on this run.</summary>
    Installed,

    /// <summary>The release feed could not be reached; nothing changed.</summary>
    Offline,

    /// <summary>Something went wrong. Message says what, and the installed build is untouched.</summary>
    Failed,
}

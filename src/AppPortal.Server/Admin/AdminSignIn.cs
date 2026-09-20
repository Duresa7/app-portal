namespace AppPortal.Server.Admin;

/// <summary>
/// The one place a user name and password become an administrator, used by the sign-in page and the admin
/// JSON API alike. Local accounts are checked first: a directory that is down or slow must never be able
/// to lock the portal, and the local check costs one hash.
/// </summary>
public sealed class AdminSignIn(AdminStore admins, IDirectoryAuthenticator directory, ILogger<AdminSignIn> logger)
{
    public AdminRecord? Authenticate(string username, string password)
    {
        var local = admins.Verify(username, password);
        if (local is not null)
        {
            return local;
        }

        if (!directory.Enabled)
        {
            return null;
        }

        var result = directory.Authenticate(username, password);
        switch (result.Outcome)
        {
            case DirectoryOutcome.Success when result.User is { } user:
                return Admit(user);

            case DirectoryOutcome.NotInGroup:
                logger.LogInformation("Directory sign-in for {Username} refused: {Detail}", username, result.Detail);
                return null;

            case DirectoryOutcome.Unavailable:
                logger.LogWarning("Directory sign-in for {Username} could not be checked: {Detail}", username, result.Detail);
                return null;

            default:
                return null;
        }
    }

    private AdminRecord? Admit(DirectoryUser user)
    {
        var existing = admins.Find(user.Username);
        if (existing is not null && !existing.IsDirectory)
        {
            // A local account owns that name. Signing in with a directory password would silently take it
            // over, so the answer is no until somebody renames one of them.
            logger.LogWarning(
                "Directory sign-in for {Username} refused: a local administrator already uses that name", user.Username);
            return null;
        }

        var record = existing ?? admins.EnsureDirectory(user.Username);
        if (record.Disabled)
        {
            logger.LogInformation("Directory sign-in for {Username} refused: the portal account is disabled", record.Username);
            return null;
        }

        if (existing is null)
        {
            logger.LogInformation("Directory account {Username} ({Display}) administers the portal for the first time",
                record.Username, user.DisplayName);
        }

        return record;
    }
}

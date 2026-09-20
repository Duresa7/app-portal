using System.Net;
using System.Text.RegularExpressions;

using AppPortal.Server.Admin;
using AppPortal.Server.Data;

using Microsoft.AspNetCore.Mvc.Testing;

namespace AppPortal.Server.Tests;

/// <summary>A migrated database in a temporary folder, and the settings a test server needs to find it.</summary>
public sealed class TestDatabase : IDisposable
{
    /// <summary>The administrator every page test signs in as.</summary>
    public const string AdminUsername = "admin";

    public const string AdminPassword = "a-long-enough-password";

    private static readonly Regex AntiforgeryField = new("name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");

    public TestDatabase(string? root = null)
    {
        Root = root ?? Path.Combine(Path.GetTempPath(), "app-portal-tests", Guid.NewGuid().ToString("N"));
        DataDirectory = Path.Combine(Root, "data");
        Directory.CreateDirectory(DataDirectory);
        Database = new Database(Path.Combine(DataDirectory, AppPortal.Server.Data.Database.FileName));
        Database.Migrate();
    }

    public string Root { get; }

    public string DataDirectory { get; }

    public Database Database { get; }

    /// <summary>Adds the administrator <see cref="SignedIn"/> signs in as.</summary>
    public void AddAdmin() => new AdminStore(Database).Add(AdminUsername, AdminPassword);

    /// <summary>A browser that has signed in as the administrator, with redirects left for the test to inspect.</summary>
    public static async Task<HttpClient> SignedIn(WebApplicationFactory<Program> factory)
    {
        var client = Browser(factory);
        var response = await client.PostAsync("/admin/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Username"] = AdminUsername,
            ["Password"] = AdminPassword,
            ["__RequestVerificationToken"] = await TokenOn(client, "/admin/login"),
        }));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        return client;
    }

    /// <summary>A browser that has not signed in and does not follow redirects.</summary>
    public static HttpClient Browser(WebApplicationFactory<Program> factory)
        => factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    /// <summary>The antiforgery token the form on a page carries, so a test can post to it.</summary>
    public static async Task<string> TokenOn(HttpClient client, string path)
    {
        var match = AntiforgeryField.Match(await client.GetStringAsync(path));
        Assert.True(match.Success, $"The form on {path} carried no antiforgery token.");
        return match.Groups[1].Value;
    }

    public void Dispose()
    {
        // Only this test's own file. Clearing every pool in the process reaches the databases of the
        // test classes running beside this one, and disposes connections they are in the middle of using.
        Database.ClearPool();
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

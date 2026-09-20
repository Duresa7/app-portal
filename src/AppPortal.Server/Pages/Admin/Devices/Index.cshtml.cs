using AppPortal.Server.Admin;
using AppPortal.Server.Admin.Lists;
using AppPortal.Server.Devices;
using AppPortal.Server.Enrollment;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AppPortal.Server.Pages.Admin.Devices;

[Authorize(Policy = AdminAuth.Policy)]
public sealed class IndexModel(DeviceStore devices, EnrollmentKeyStore keys) : AdminListPage<SearchFilter, DeviceRecord>
{
    public IReadOnlyDictionary<string, int> InstallCounts { get; private set; } = new Dictionary<string, int>();

    private IReadOnlyDictionary<string, string> EnrollmentKeys { get; set; } = new Dictionary<string, string>();

    /// <summary>Shown once, straight after adding a device. Never stored and never shown again.</summary>
    public string? IssuedToken { get; private set; }

    public string? IssuedFor { get; private set; }

    [BindProperty]
    public string NewName { get; set; } = "";

    [BindProperty]
    public string NewEndpointId { get; set; } = "";

    public IActionResult OnPostAdd()
    {
        var name = (NewName ?? "").Trim();
        if (name.Length == 0)
        {
            Error = "A device needs a name.";
            Load();
            return Page();
        }

        if (devices.FindByName(name) is not null)
        {
            Error = $"A device called '{name}' is already registered. Rotate its token from its own page instead.";
            Load();
            return Page();
        }

        IssuedToken = devices.Add(name, (NewEndpointId ?? "").Trim());
        IssuedFor = name;
        Notice = $"'{name}' is registered.";
        NewName = "";
        NewEndpointId = "";
        Load();
        return Page();
    }

    protected override void Load()
    {
        Slice = devices.List(Filter, Query);
        InstallCounts = devices.InstallCounts();
        EnrollmentKeys = keys.List().ToDictionary(key => key.Id, key => key.Name);
    }

    public string EnrollmentKeyFor(DeviceRecord device)
        => device.EnrolledWithKeyId is not { } id
            ? "added by hand"
            : EnrollmentKeys.TryGetValue(id, out var name) ? name : id;

    public int InstallsFor(DeviceRecord device)
        => InstallCounts.TryGetValue(device.Id, out var count) ? count : 0;
}

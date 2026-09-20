using AppPortal.Server.Admin;
using AppPortal.Server.Devices;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AppPortal.Server.Pages.Admin.Devices;

[Authorize(Policy = AdminAuth.Policy)]
public sealed class IndexModel(DeviceStore devices) : PageModel
{
    public IReadOnlyList<DeviceRecord> Devices { get; private set; } = [];

    public IReadOnlyDictionary<string, int> InstallCounts { get; private set; } = new Dictionary<string, int>();

    public string? Error { get; private set; }

    public string? Notice { get; private set; }

    /// <summary>Shown once, straight after adding a device. Never stored and never shown again.</summary>
    public string? IssuedToken { get; private set; }

    public string? IssuedFor { get; private set; }

    [BindProperty(SupportsGet = true)]
    public string Search { get; set; } = "";

    [BindProperty]
    public string NewName { get; set; } = "";

    [BindProperty]
    public string NewEndpointId { get; set; } = "";

    public void OnGet() => Load();

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

    private void Load()
    {
        Devices = devices.Search(Search);
        InstallCounts = devices.InstallCounts();
    }

    public int InstallsFor(DeviceRecord device)
        => InstallCounts.TryGetValue(device.Id, out var count) ? count : 0;
}

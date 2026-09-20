using AppPortal.Server.Admin;
using AppPortal.Server.Devices;
using AppPortal.Server.Installs;
using AppPortal.Server.Requests;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AppPortal.Server.Pages.Admin.Devices;

[Authorize(Policy = AdminAuth.Policy)]
public sealed class DetailModel(DeviceStore devices, InstallStore installs, AppRequestStore requests) : PageModel
{
    /// <summary>What the engine preference dropdown offers. Empty means follow the server.</summary>
    public static readonly string[] EnginePreferences = ["", "action1", "agent"];

    public DeviceRecord Device { get; private set; } = null!;

    public IReadOnlyList<InstallRecord> RecentInstalls { get; private set; } = [];

    public IReadOnlyList<AppRequestRecord> RecentRequests { get; private set; } = [];

    public string? Error { get; private set; }

    public string? Notice { get; private set; }

    /// <summary>Set only by a rotation, and only for the one render that follows it.</summary>
    public string? IssuedToken { get; private set; }

    [BindProperty]
    public string Name { get; set; } = "";

    [BindProperty]
    public string EndpointId { get; set; } = "";

    [BindProperty]
    public bool Enabled { get; set; }

    [BindProperty]
    public string EnginePreference { get; set; } = "";

    public IActionResult OnGet(string id)
    {
        if (!Load(id))
        {
            return NotFound();
        }

        Fill();
        return Page();
    }

    public IActionResult OnPostSave(string id)
    {
        if (!Load(id))
        {
            return NotFound();
        }

        try
        {
            devices.Update(new DeviceRecord
            {
                Id = Device.Id,
                Name = Name ?? "",
                EndpointId = EndpointId ?? "",
                Enabled = Enabled,
                EnginePreference = string.IsNullOrWhiteSpace(EnginePreference) ? null : EnginePreference,
            });
            Notice = "Saved.";
        }
        catch (DeviceRejectedException ex)
        {
            Error = ex.Message;
            Load(id);
            return Page();
        }

        Load(id);
        Fill();
        return Page();
    }

    public IActionResult OnPostRotate(string id)
    {
        if (!Load(id))
        {
            return NotFound();
        }

        IssuedToken = devices.RotateToken(Device.Id);
        Notice = IssuedToken is null
            ? null
            : "A new token is issued. The old one stopped working just now, so the device cannot call in until it has this one.";
        Fill();
        return Page();
    }

    public IActionResult OnPostRemove(string id)
    {
        if (!Load(id))
        {
            return NotFound();
        }

        if (devices.RemoveById(Device.Id))
        {
            if (AdminListPage.IsHtmx(Request))
            {
                Response.Headers["HX-Redirect"] = "/admin/devices";
                return new EmptyResult();
            }

            return RedirectToPage("Index");
        }

        Error = "This device has an install still running. Wait for it to finish, or disable the device instead.";
        Fill();
        return Page();
    }

    private bool Load(string id)
    {
        var record = devices.Find(id);
        if (record is null)
        {
            return false;
        }

        Device = record;
        RecentInstalls = [.. installs.ForDeviceId(record.Id).Take(20)];
        RecentRequests = [.. requests.ListForDeviceId(record.Id).Take(20)];
        return true;
    }

    private void Fill()
    {
        Name = Device.Name;
        EndpointId = Device.EndpointId;
        Enabled = Device.Enabled;
        EnginePreference = Device.EnginePreference ?? "";
    }
}

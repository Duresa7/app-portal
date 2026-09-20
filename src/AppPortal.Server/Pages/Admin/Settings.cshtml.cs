using AppPortal.Server.Admin;
using AppPortal.Server.Settings;
using AppPortal.Shared;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AppPortal.Server.Pages.Admin;

[Authorize(Policy = AdminAuth.Policy)]
public sealed class SettingsModel(SettingsStore settings) : PageModel
{
    [BindProperty]
    public string DefaultEngine { get; set; } = EngineLabel.Action1;

    public string? Saved { get; private set; }

    public string? Error { get; private set; }

    public void OnGet() => DefaultEngine = settings.DefaultEngine;

    public IActionResult OnPost()
    {
        var chosen = (DefaultEngine ?? "").ToLowerInvariant();
        if (chosen is not (EngineLabel.Action1 or EngineLabel.Agent))
        {
            // Anything else would be stored and then quietly ignored by the selector, which is worse
            // than refusing it: the page would show a setting that does nothing.
            Error = "Choose either Action1 or Agent.";
            DefaultEngine = settings.DefaultEngine;
            return Page();
        }

        settings.Set(SettingsStore.DefaultEngineKey, chosen);
        DefaultEngine = chosen;
        Saved = "Saved.";
        return Page();
    }
}

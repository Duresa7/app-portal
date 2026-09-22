using AppPortal.Client.Services;

namespace AppPortal.Client.ViewModels.Admin;

/// <summary>
/// A page that has its place in the navigation but not its content yet. The package that builds the
/// page derives its view model from <see cref="AdminPageViewModel"/> instead; once no page derives from
/// this, it and its panel can go.
/// </summary>
public abstract class ComingSoonPageViewModel(IAdminApiClient api, string title, string summary) : AdminPageViewModel(api)
{
    public string Title { get; } = title;

    /// <summary>What the page will do, in one sentence, so the placeholder says more than "not yet".</summary>
    public string Summary { get; } = summary;
}

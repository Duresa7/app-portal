using AppPortal.Server.Catalog;
using AppPortal.Server.Devices;
using AppPortal.Server.Installs;
using AppPortal.Shared;

namespace AppPortal.Server.Tests;

/// <summary>
/// The rule that decides which engine runs an install, over every combination that can occur. It is a
/// ladder on purpose, so that "why did it go that way" is answered by reading it in order, and a table
/// is the only way to be sure the ladder has no rung that is never reached.
/// </summary>
public sealed class EngineSelectorTests
{
    [Theory]
    // Nothing can install it.
    [InlineData(false, false, false, false, null, null, "action1", null)]
    // One engine can, and no preference is interesting when there is no choice to make.
    [InlineData(true, false, true, false, null, null, "action1", "action1")]
    [InlineData(false, true, false, true, null, null, "action1", "agent")]
    [InlineData(false, true, false, true, "action1", "action1", "action1", "agent")]
    [InlineData(true, false, true, false, "agent", "agent", "agent", "action1")]
    // The app has a package the device cannot use.
    [InlineData(true, true, true, false, null, null, "action1", "action1")]
    [InlineData(true, true, false, true, null, null, "action1", "agent")]
    // Both can. The server default decides when nobody else has.
    [InlineData(true, true, true, true, null, null, "action1", "action1")]
    [InlineData(true, true, true, true, null, null, "agent", "agent")]
    // The app overrides the server.
    [InlineData(true, true, true, true, null, "agent", "action1", "agent")]
    [InlineData(true, true, true, true, null, "action1", "agent", "action1")]
    // The device overrides the app, which overrides the server.
    [InlineData(true, true, true, true, "agent", "action1", "action1", "agent")]
    [InlineData(true, true, true, true, "action1", "agent", "agent", "action1")]
    // A preference nobody can honour is passed over rather than obeyed.
    [InlineData(true, true, true, false, "agent", null, "action1", "action1")]
    [InlineData(true, true, false, true, "action1", null, "agent", "agent")]
    [InlineData(true, true, true, true, "nonsense", null, "agent", "agent")]
    [InlineData(true, true, true, true, null, "nonsense", "agent", "agent")]
    // Both available and nothing at all chose: Action1, the engine that was there first.
    [InlineData(true, true, true, true, null, null, "nonsense", "action1")]
    [InlineData(true, true, true, true, null, null, null, "action1")]
    public void The_ladder_is_read_in_order(bool appAction1, bool appAgent, bool deviceAction1, bool deviceAgent,
        string? devicePreference, string? appOverride, string? serverDefault, string? expected)
    {
        var device = new DeviceRecord
        {
            Id = "d1",
            Name = "PC",
            EndpointId = deviceAction1 ? "endpoint-1" : "",
            HasAgent = deviceAgent,
            EnginePreference = devicePreference,
        };
        var app = new CatalogEntry
        {
            Id = "app",
            Name = "App",
            Action1 = appAction1 ? new Action1PackageRef { PackageId = "pkg" } : new Action1PackageRef(),
            Agent = appAgent ? new WingetPackageDefinition("Vendor.App", "machine") : null,
            EngineOverride = appOverride,
        };

        Assert.Equal(expected, EngineSelector.Choose(device, app, serverDefault));
    }

    [Theory]
    [InlineData("ACTION1")]
    [InlineData("Agent")]
    public void A_preference_is_read_whatever_case_it_was_written_in(string preference)
    {
        // The database, the JSON and the UI all say these in lower case, but a value typed by hand or
        // carried in from an import should not silently mean "no preference".
        var device = new DeviceRecord { Id = "d1", Name = "PC", EndpointId = "endpoint-1", HasAgent = true, EnginePreference = preference };
        var app = new CatalogEntry
        {
            Id = "app",
            Name = "App",
            Action1 = new Action1PackageRef { PackageId = "pkg" },
            Agent = new WingetPackageDefinition("Vendor.App", "machine"),
        };

        Assert.Equal(preference.ToLowerInvariant(), EngineSelector.Choose(device, app, "action1"));
    }
}

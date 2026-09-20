using AppPortal.Server.Devices;
using AppPortal.Server.Installs;
using AppPortal.Shared;

namespace AppPortal.Server.Tests;

public sealed class InstallStoreQueryTests : IDisposable
{
    private readonly TestDatabase _test = new();
    private readonly InstallStore _installs;

    public InstallStoreQueryTests()
    {
        _installs = new InstallStore(_test.Database);
        var devices = new DeviceStore(_test.Database);
        devices.Add("PC-A", "endpoint-a");
        devices.Add("PC-B", "endpoint-b");
    }

    private InstallRecord Seed(
        string device,
        string appId,
        InstallState state,
        DateTimeOffset requestedAt,
        string? requestedBy = null)
    {
        var record = new InstallRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            DeviceName = device,
            AppId = appId,
            AppName = appId.ToUpperInvariant(),
            State = state,
            RequestedAt = requestedAt,
            CompletedAt = state is InstallState.Queued or InstallState.Running ? null : requestedAt.AddMinutes(2),
            RequestedBy = requestedBy,
        };
        Assert.True(_installs.Upsert(record));
        return record;
    }

    [Fact]
    public void List_recent_is_newest_first()
    {
        var now = DateTimeOffset.UtcNow;
        Seed("PC-A", "chrome", InstallState.Succeeded, now.AddHours(-3));
        Seed("PC-A", "vlc", InstallState.Succeeded, now.AddHours(-1));
        Seed("PC-B", "7-zip", InstallState.Succeeded, now.AddHours(-2));

        var listed = _installs.ListRecent(InstallFilter.None, 100, 0);

        Assert.Equal(["vlc", "7-zip", "chrome"], listed.Select(i => i.AppId));
    }

    [Fact]
    public void Filtering_by_state_returns_only_that_state()
    {
        var now = DateTimeOffset.UtcNow;
        Seed("PC-A", "chrome", InstallState.Failed, now.AddHours(-3));
        Seed("PC-A", "vlc", InstallState.Succeeded, now.AddHours(-2));
        Seed("PC-B", "7-zip", InstallState.Failed, now.AddHours(-1));

        var failed = _installs.ListRecent(new InstallFilter(State: InstallState.Failed), 100, 0);

        Assert.Equal(2, failed.Count);
        Assert.All(failed, i => Assert.Equal(InstallState.Failed, i.State));
        Assert.Equal(2, _installs.CountMatching(new InstallFilter(State: InstallState.Failed)));
    }

    [Fact]
    public void Filtering_by_device_and_app_narrows_to_one_row()
    {
        var now = DateTimeOffset.UtcNow;
        Seed("PC-A", "chrome", InstallState.Succeeded, now.AddHours(-3));
        Seed("PC-B", "chrome", InstallState.Succeeded, now.AddHours(-2));
        Seed("PC-A", "vlc", InstallState.Succeeded, now.AddHours(-1));

        var mine = _installs.ListRecent(new InstallFilter(Device: "pc-a", AppId: "CHROME"), 100, 0);

        var only = Assert.Single(mine);
        Assert.Equal("PC-A", only.DeviceName);
        Assert.Equal("chrome", only.AppId);
    }

    [Fact]
    public void Requester_matches_on_any_part_of_the_account_name()
    {
        var now = DateTimeOffset.UtcNow;
        Seed("PC-A", "chrome", InstallState.Succeeded, now.AddHours(-2), @"CONTOSO\jdoe");
        Seed("PC-B", "vlc", InstallState.Succeeded, now.AddHours(-1), @"CONTOSO\asmith");

        Assert.Equal("chrome", Assert.Single(_installs.ListRecent(new InstallFilter(Requester: "jdoe"), 100, 0)).AppId);
        Assert.Equal(2, _installs.ListRecent(new InstallFilter(Requester: "CONTOSO"), 100, 0).Count);
        Assert.Empty(_installs.ListRecent(new InstallFilter(Requester: "nobody"), 100, 0));
    }

    [Fact]
    public void A_wildcard_in_the_requester_box_is_matched_literally()
    {
        var now = DateTimeOffset.UtcNow;
        Seed("PC-A", "chrome", InstallState.Succeeded, now.AddHours(-2), @"CONTOSO\jdoe");

        // Without escaping, LIKE would read this as "match anything" and return the row.
        Assert.Empty(_installs.ListRecent(new InstallFilter(Requester: "%"), 100, 0));
        Assert.Empty(_installs.ListRecent(new InstallFilter(Requester: "j_oe"), 100, 0));
    }

    [Fact]
    public void A_date_range_excludes_what_falls_outside_it()
    {
        var now = DateTimeOffset.UtcNow;
        Seed("PC-A", "old", InstallState.Succeeded, now.AddDays(-10));
        Seed("PC-A", "recent", InstallState.Succeeded, now.AddDays(-1));

        var lastWeek = _installs.ListRecent(new InstallFilter(From: now.AddDays(-7)), 100, 0);
        Assert.Equal("recent", Assert.Single(lastWeek).AppId);

        var longAgo = _installs.ListRecent(new InstallFilter(To: now.AddDays(-5)), 100, 0);
        Assert.Equal("old", Assert.Single(longAgo).AppId);
    }

    [Fact]
    public void Paging_walks_the_history_without_repeating_or_skipping()
    {
        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < 10; i++)
        {
            Seed("PC-A", $"app-{i}", InstallState.Succeeded, now.AddMinutes(-i));
        }

        var first = _installs.ListRecent(InstallFilter.None, 4, 0);
        var second = _installs.ListRecent(InstallFilter.None, 4, 4);
        var third = _installs.ListRecent(InstallFilter.None, 4, 8);

        Assert.Equal(4, first.Count);
        Assert.Equal(4, second.Count);
        Assert.Equal(2, third.Count);
        Assert.Equal(10, first.Concat(second).Concat(third).Select(i => i.Id).Distinct().Count());
        Assert.Equal(10, _installs.CountMatching(InstallFilter.None));
    }

    [Fact]
    public void Count_by_state_and_since_feeds_the_dashboard()
    {
        var now = DateTimeOffset.UtcNow;
        Seed("PC-A", "a", InstallState.Failed, now.AddDays(-1));
        Seed("PC-A", "b", InstallState.Failed, now.AddDays(-30));
        Seed("PC-A", "c", InstallState.Succeeded, now.AddHours(-1));
        Seed("PC-B", "d", InstallState.Running, now.AddMinutes(-5));
        Seed("PC-B", "e", InstallState.Queued, now.AddMinutes(-2));

        Assert.Equal(1, _installs.CountBy(InstallState.Failed, now.AddDays(-7)));
        Assert.Equal(2, _installs.CountBy(InstallState.Failed, null));
        Assert.Equal(5, _installs.CountBy(null, null));
        Assert.Equal(2, _installs.CountActive());
    }

    [Fact]
    public void The_engine_comes_back_with_the_record_and_has_a_label()
    {
        Seed("PC-A", "chrome", InstallState.Succeeded, DateTimeOffset.UtcNow);

        var record = Assert.Single(_installs.ListRecent(InstallFilter.None, 100, 0));

        Assert.Equal(EngineLabel.Action1, record.Engine);
        Assert.Equal("via Action1", record.EngineText);
    }

    public void Dispose() => _test.Dispose();
}

using AppPortal.Server.Admin.Lists;
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

        var listed = _installs.List(InstallFilter.None, ListQuery.All).Rows;

        Assert.Equal(["vlc", "7-zip", "chrome"], listed.Select(i => i.AppId));
    }

    [Fact]
    public void Filtering_by_state_returns_only_that_state()
    {
        var now = DateTimeOffset.UtcNow;
        Seed("PC-A", "chrome", InstallState.Failed, now.AddHours(-3));
        Seed("PC-A", "vlc", InstallState.Succeeded, now.AddHours(-2));
        Seed("PC-B", "7-zip", InstallState.Failed, now.AddHours(-1));

        var failed = _installs.List(new InstallFilter(State: InstallState.Failed), ListQuery.All).Rows;

        Assert.Equal(2, failed.Count);
        Assert.All(failed, i => Assert.Equal(InstallState.Failed, i.State));
        Assert.Equal(2, _installs.List(new InstallFilter(State: InstallState.Failed), Counted).Total);
    }

    [Fact]
    public void Filtering_by_device_and_app_narrows_to_one_row()
    {
        var now = DateTimeOffset.UtcNow;
        Seed("PC-A", "chrome", InstallState.Succeeded, now.AddHours(-3));
        Seed("PC-B", "chrome", InstallState.Succeeded, now.AddHours(-2));
        Seed("PC-A", "vlc", InstallState.Succeeded, now.AddHours(-1));

        var mine = _installs.List(new InstallFilter(Device: "pc-a", AppId: "CHROME"), ListQuery.All).Rows;

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

        Assert.Equal("chrome", Assert.Single(_installs.List(new InstallFilter(Requester: "jdoe"), ListQuery.All).Rows).AppId);
        Assert.Equal(2, _installs.List(new InstallFilter(Requester: "CONTOSO"), ListQuery.All).Rows.Count);
        Assert.Empty(_installs.List(new InstallFilter(Requester: "nobody"), ListQuery.All).Rows);
    }

    [Fact]
    public void A_wildcard_in_the_requester_box_is_matched_literally()
    {
        var now = DateTimeOffset.UtcNow;
        Seed("PC-A", "chrome", InstallState.Succeeded, now.AddHours(-2), @"CONTOSO\jdoe");

        // Without escaping, LIKE would read this as "match anything" and return the row.
        Assert.Empty(_installs.List(new InstallFilter(Requester: "%"), ListQuery.All).Rows);
        Assert.Empty(_installs.List(new InstallFilter(Requester: "j_oe"), ListQuery.All).Rows);
    }

    [Fact]
    public void A_date_range_excludes_what_falls_outside_it()
    {
        var now = DateTimeOffset.UtcNow;
        Seed("PC-A", "old", InstallState.Succeeded, now.AddDays(-10));
        Seed("PC-A", "recent", InstallState.Succeeded, now.AddDays(-1));

        var lastWeek = _installs.List(new InstallFilter(From: Day(now.AddDays(-7))), ListQuery.All).Rows;
        Assert.Equal("recent", Assert.Single(lastWeek).AppId);

        var longAgo = _installs.List(new InstallFilter(To: Day(now.AddDays(-5))), ListQuery.All).Rows;
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

        var first = _installs.List(InstallFilter.None, new ListQuery(4, 0, null, WantTotal: true));
        var second = _installs.List(InstallFilter.None, new ListQuery(4, 4, null, false));
        var third = _installs.List(InstallFilter.None, new ListQuery(4, 8, null, false));

        Assert.Equal(4, first.Rows.Count);
        Assert.True(first.HasMore);
        Assert.Equal(10, first.Total);
        Assert.Equal(4, second.Rows.Count);
        Assert.True(second.HasMore);
        Assert.Null(second.Total);
        Assert.Equal(2, third.Rows.Count);
        Assert.False(third.HasMore);
        Assert.Equal(8, third.Offset);
        Assert.Equal(10, first.Rows.Concat(second.Rows).Concat(third.Rows).Select(i => i.Id).Distinct().Count());
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

        var record = Assert.Single(_installs.List(InstallFilter.None, ListQuery.All).Rows);

        Assert.Equal(EngineLabel.Action1, record.Engine);
        Assert.Equal("via Action1", record.EngineText);
    }

    [Fact]
    public void A_slice_at_an_offset_is_the_whole_list_with_that_many_skipped()
    {
        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < 130; i++)
        {
            Seed(i % 2 == 0 ? "PC-A" : "PC-B", $"app-{i:D3}", InstallState.Succeeded, now.AddMinutes(-i));
        }

        var whole = _installs.List(InstallFilter.None, ListQuery.All).Rows;
        var slice = _installs.List(InstallFilter.None, new ListQuery(50, 100, null, false));

        Assert.Equal(whole.Skip(100).Take(50).Select(i => i.Id), slice.Rows.Select(i => i.Id));
        Assert.Equal(30, slice.Rows.Count);
        Assert.False(slice.HasMore);
        Assert.True(_installs.List(InstallFilter.None, new ListQuery(50, 50, null, false)).HasMore);
    }

    [Fact]
    public void A_sort_changes_the_order_and_an_undeclared_column_is_refused()
    {
        var now = DateTimeOffset.UtcNow;
        Seed("PC-A", "vlc", InstallState.Succeeded, now.AddHours(-3));
        Seed("PC-B", "chrome", InstallState.Failed, now.AddHours(-2));
        Seed("PC-A", "7-zip", InstallState.Queued, now.AddHours(-1));

        Assert.Equal(["7-zip", "chrome", "vlc"], _installs.List(InstallFilter.None, ListQuery.All).Rows.Select(i => i.AppId));
        Assert.Equal(["7-zip", "chrome", "vlc"], _installs.List(InstallFilter.None, ListQuery.All with { Sort = "app" }).Rows.Select(i => i.AppId));
        Assert.Equal(["vlc", "chrome", "7-zip"], _installs.List(InstallFilter.None, ListQuery.All with { Sort = "-app" }).Rows.Select(i => i.AppId));
        Assert.Equal(["7-zip", "vlc", "chrome"], _installs.List(InstallFilter.None, ListQuery.All with { Sort = "device" }).Rows.Select(i => i.AppId));

        Assert.Throws<UnknownSortException>(() => _installs.List(InstallFilter.None, ListQuery.All with { Sort = "requested_at" }));
    }

    /// <summary>Counted, because a page that says "of 340" needs the total; most callers do not.</summary>
    private static readonly ListQuery Counted = ListQuery.All with { WantTotal = true };

    /// <summary>The day a moment falls on where the server is, which is how the filter reads a date box.</summary>
    private static DateOnly Day(DateTimeOffset moment) => DateOnly.FromDateTime(moment.LocalDateTime);

    public void Dispose() => _test.Dispose();
}

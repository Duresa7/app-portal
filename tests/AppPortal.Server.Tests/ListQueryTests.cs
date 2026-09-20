using AppPortal.Server.Admin.Lists;
using AppPortal.Server.Installs;
using AppPortal.Server.Requests;
using AppPortal.Shared;

namespace AppPortal.Server.Tests;

public sealed class ListQueryTests
{
    private static readonly IReadOnlyList<int> Ten = [.. Enumerable.Range(0, 10)];

    [Fact]
    public void The_whole_list_is_the_default_query()
    {
        var slice = Slice.Of(Ten, ListQuery.All);

        Assert.Equal(Ten, slice.Rows);
        Assert.Equal(0, slice.Offset);
        Assert.False(slice.HasMore);
        Assert.Null(slice.Total);
    }

    [Fact]
    public void A_slice_cut_in_memory_is_the_list_with_the_offset_skipped()
    {
        var slice = Slice.Of(Ten, new ListQuery(4, 4, null, WantTotal: true));

        Assert.Equal(Ten.Skip(4).Take(4), slice.Rows);
        Assert.Equal(4, slice.Offset);
        Assert.True(slice.HasMore);
        Assert.Equal(10, slice.Total);
    }

    [Fact]
    public void The_last_slice_says_nothing_follows_and_an_offset_past_the_end_is_empty()
    {
        var last = Slice.Of(Ten, new ListQuery(4, 8, null, false));
        var beyond = Slice.Of(Ten, new ListQuery(4, 40, null, true));

        Assert.Equal([8, 9], last.Rows);
        Assert.False(last.HasMore);
        Assert.Empty(beyond.Rows);
        Assert.Equal(40, beyond.Offset);
        Assert.False(beyond.HasMore);
        Assert.Equal(10, beyond.Total);
    }

    [Fact]
    public void A_lookahead_window_drops_the_extra_row_and_keeps_what_it_learned()
    {
        var query = new ListQuery(3, 6, null, false);
        Assert.Equal(4, Slice.Lookahead(query));
        Assert.Equal(-1, Slice.Lookahead(ListQuery.All));

        var more = Slice.FromLookahead([1, 2, 3, 4], query, null);
        var end = Slice.FromLookahead([1, 2], query, 8);

        Assert.Equal([1, 2, 3], more.Rows);
        Assert.True(more.HasMore);
        Assert.Equal(6, more.Offset);
        Assert.Equal([1, 2], end.Rows);
        Assert.False(end.HasMore);
        Assert.Equal(8, end.Total);
    }

    [Fact]
    public void A_query_refuses_a_limit_below_one_and_a_negative_offset()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ListQuery(0, 0, null, false));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ListQuery(10, -1, null, false));
    }

    [Fact]
    public void Row_paging_reads_and_writes_the_skip_parameter()
    {
        var paging = Paging.ByRows("Skip", 100, wantTotal: true);

        var query = paging.Read(new Dictionary<string, string?> { ["Skip"] = "200" });
        Assert.Equal(new ListQuery(100, 200, null, true), query);

        var values = new Dictionary<string, string?>();
        paging.Write(values, 200);
        Assert.Equal("200", values["Skip"]);

        paging.Write(values = [], 0);
        Assert.Empty(values);
    }

    [Fact]
    public void Page_paging_reads_and_writes_a_page_number()
    {
        var paging = Paging.ByPages("p", 50);

        Assert.Equal(100, paging.Read(new Dictionary<string, string?> { ["p"] = "3" }).Offset);
        Assert.Equal(0, paging.Read(new Dictionary<string, string?> { ["p"] = "1" }).Offset);
        Assert.False(paging.Read(new Dictionary<string, string?>()).WantTotal);

        var values = new Dictionary<string, string?>();
        paging.Write(values, 100);
        Assert.Equal("3", values["p"]);
        Assert.Equal(3, paging.PageNumber(100));
    }

    [Theory]
    [InlineData("-5")]
    [InlineData("abc")]
    [InlineData("")]
    [InlineData(null)]
    public void Anything_that_is_not_a_position_is_the_start(string? skip)
    {
        var values = new Dictionary<string, string?>();
        if (skip is not null)
        {
            values["Skip"] = skip;
        }

        Assert.Equal(0, Paging.ByRows("Skip", 100).Read(values).Offset);
    }

    [Fact]
    public void A_link_is_written_in_the_order_the_values_were_and_blanks_are_left_out()
    {
        var values = new Dictionary<string, string?> { ["Device"] = "PC A", ["App"] = "", ["Skip"] = "100" };

        Assert.Equal("?Device=PC%20A&Skip=100", QueryValues.ToQueryString(values));
        Assert.Equal("", QueryValues.ToQueryString(new Dictionary<string, string?>()));
    }

    [Fact]
    public void Sort_columns_give_the_default_when_nothing_is_asked_and_refuse_what_was_not_declared()
    {
        var sorts = new SortColumns("i.requested_at DESC", ("device", "device_name"), ("app", "i.app_name"));

        Assert.Equal(" ORDER BY i.requested_at DESC", sorts.OrderBy(null));
        Assert.Equal(" ORDER BY device_name ASC, i.requested_at DESC", sorts.OrderBy("device"));
        Assert.Equal(" ORDER BY i.app_name DESC, i.requested_at DESC", sorts.OrderBy("-App"));

        var refused = Assert.Throws<UnknownSortException>(() => sorts.OrderBy("requested_at; DROP TABLE installs"));
        Assert.Contains("device", refused.Message);
    }

    [Fact]
    public void The_install_filter_round_trips_through_a_query_string()
    {
        var filter = new InstallFilter("PC-A", "chrome", InstallState.Failed, "jdoe", new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 20));

        var values = new Dictionary<string, string?>();
        filter.Write(values);

        Assert.Equal("?Device=PC-A&App=chrome&State=Failed&Requester=jdoe&From=2026-09-01&To=2026-09-20", QueryValues.ToQueryString(values));
        Assert.Equal(filter, InstallFilter.Read(values));
    }

    [Fact]
    public void An_empty_install_filter_writes_nothing_and_reads_back_from_nothing()
    {
        var values = new Dictionary<string, string?>();
        InstallFilter.None.Write(values);

        Assert.Empty(values);
        Assert.Equal(InstallFilter.None, InstallFilter.Read(values));
        Assert.Equal(InstallFilter.None, InstallFilter.Read(new Dictionary<string, string?> { ["State"] = "nonsense", ["From"] = "yesterday" }));
    }

    [Fact]
    public void The_install_filter_reads_a_state_regardless_of_case()
    {
        var filter = InstallFilter.Read(new Dictionary<string, string?> { ["State"] = "failed" });

        Assert.Equal(InstallState.Failed, filter.State);
    }

    [Fact]
    public void The_search_filter_round_trips_and_matches_any_field()
    {
        var filter = new SearchFilter("chrome");
        var values = new Dictionary<string, string?>();
        filter.Write(values);

        Assert.Equal("?Search=chrome", QueryValues.ToQueryString(values));
        Assert.Equal(filter, SearchFilter.Read(values));
        Assert.True(filter.Matches("7-zip", "Google Chrome"));
        Assert.False(filter.Matches("7-zip", null));
        Assert.True(SearchFilter.None.Matches("anything"));
        Assert.Equal(SearchFilter.None, SearchFilter.Read(new Dictionary<string, string?> { ["Search"] = "  " }));
    }

    [Theory]
    [InlineData("pending", AppRequestStatus.Pending)]
    [InlineData("approved", AppRequestStatus.Approved)]
    [InlineData("denied", AppRequestStatus.Denied)]
    [InlineData("all", null)]
    public void The_request_filter_round_trips_through_its_tab(string tab, AppRequestStatus? status)
    {
        var filter = new RequestFilter(status);
        var values = new Dictionary<string, string?>();
        filter.Write(values);

        Assert.Equal(tab, values["tab"]);
        Assert.Equal(filter, RequestFilter.Read(values));
    }

    [Fact]
    public void An_unknown_tab_is_pending_rather_than_nothing()
    {
        Assert.Equal(RequestFilter.Pending, RequestFilter.Read(new Dictionary<string, string?> { ["tab"] = "nonsense" }));
        Assert.Equal(RequestFilter.Pending, RequestFilter.Read(new Dictionary<string, string?>()));
    }

    [Fact]
    public void No_filter_writes_nothing()
    {
        var values = new Dictionary<string, string?>();
        NoFilter.Instance.Write(values);

        Assert.Empty(values);
        Assert.Same(NoFilter.Instance, NoFilter.Read(values));
    }
}

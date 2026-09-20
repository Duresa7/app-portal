using AppPortal.Server.Admin;
using AppPortal.Server.Admin.Lists;
using AppPortal.Server.Catalog;
using AppPortal.Server.Devices;
using AppPortal.Server.Enrollment;
using AppPortal.Server.Requests;
using AppPortal.Shared;

namespace AppPortal.Server.Tests;

/// <summary>
/// Every administration list is served by a store method that takes the same query and returns the
/// same slice. Each is checked against a real database for the three things the page tests used to
/// read off the HTML: a slice is the list with the offset skipped, the sort is honoured or refused,
/// and the filter narrows.
/// </summary>
public sealed class StoreSliceTests : IDisposable
{
    private readonly TestDatabase _test = new();

    [Fact]
    public void Catalog_slices_sorts_and_searches()
    {
        var store = new CatalogStore(_test.Database, "");
        store.Import(CatalogStore.Parse("""
            { "apps": [
              { "id": "vlc", "name": "VLC", "publisher": "VideoLAN", "category": "Media", "action1": { "packageId": "v" } },
              { "id": "chrome", "name": "Google Chrome", "publisher": "Google", "category": "Browsers", "action1": { "packageId": "c" } },
              { "id": "7-zip", "name": "7-Zip", "publisher": "Igor Pavlov", "category": "Tools", "action1": { "packageId": "z" } } ] }
            """));

        var whole = store.List(SearchFilter.None, ListQuery.All);
        Assert.Equal(["vlc", "chrome", "7-zip"], whole.Rows.Select(a => a.Id));
        Assert.False(whole.HasMore);

        var slice = store.List(SearchFilter.None, new ListQuery(1, 1, null, WantTotal: true));
        Assert.Equal(whole.Rows.Skip(1).Take(1).Select(a => a.Id), slice.Rows.Select(a => a.Id));
        Assert.True(slice.HasMore);
        Assert.Equal(3, slice.Total);
        Assert.False(store.List(SearchFilter.None, new ListQuery(1, 2, null, false)).HasMore);

        Assert.Equal(["7-zip", "chrome", "vlc"], store.List(SearchFilter.None, ListQuery.All with { Sort = "id" }).Rows.Select(a => a.Id));
        Assert.Equal(["vlc", "7-zip", "chrome"], store.List(SearchFilter.None, ListQuery.All with { Sort = "-publisher" }).Rows.Select(a => a.Id));
        Assert.Throws<UnknownSortException>(() => store.List(SearchFilter.None, ListQuery.All with { Sort = "rowid" }));

        Assert.Equal("chrome", Assert.Single(store.List(new SearchFilter("google"), ListQuery.All).Rows).Id);
        Assert.Equal("7-zip", Assert.Single(store.List(new SearchFilter("pavlov"), ListQuery.All).Rows).Id);
        Assert.Equal("vlc", Assert.Single(store.List(new SearchFilter("media"), ListQuery.All).Rows).Id);
        Assert.Empty(store.List(new SearchFilter("nothing here"), ListQuery.All).Rows);
    }

    [Fact]
    public void Devices_slice_sort_and_search()
    {
        var store = new DeviceStore(_test.Database);
        store.Add("PC-C", "endpoint-c");
        store.Add("PC-A", "endpoint-a");
        store.Add("PC-B", "endpoint-b");

        var whole = store.List(SearchFilter.None, ListQuery.All);
        Assert.Equal(["PC-A", "PC-B", "PC-C"], whole.Rows.Select(d => d.Name));

        var slice = store.List(SearchFilter.None, new ListQuery(2, 1, null, false));
        Assert.Equal(whole.Rows.Skip(1).Take(2).Select(d => d.Id), slice.Rows.Select(d => d.Id));
        Assert.False(slice.HasMore);
        Assert.True(store.List(SearchFilter.None, new ListQuery(1, 0, null, false)).HasMore);

        Assert.Equal(["PC-C", "PC-B", "PC-A"], store.List(SearchFilter.None, ListQuery.All with { Sort = "-name" }).Rows.Select(d => d.Name));
        Assert.Equal(["PC-C", "PC-A", "PC-B"], store.List(SearchFilter.None, ListQuery.All with { Sort = "created" }).Rows.Select(d => d.Name));
        Assert.Throws<UnknownSortException>(() => store.List(SearchFilter.None, ListQuery.All with { Sort = "token_hash" }));

        Assert.Equal("PC-B", Assert.Single(store.List(new SearchFilter("c-b"), ListQuery.All).Rows).Name);
        Assert.Empty(store.List(new SearchFilter("endpoint"), ListQuery.All).Rows);
    }

    [Fact]
    public void Requests_slice_sort_and_filter_by_status()
    {
        new DeviceStore(_test.Database).Add("TESTPC", "endpoint-1");
        var store = new AppRequestStore(_test.Database);
        var first = store.Create("TESTPC", null, "first");
        var second = store.Create("TESTPC", null, "second");
        store.Create("TESTPC", null, "third");
        Assert.True(store.Decide(first.Id, AppRequestStatus.Approved, null, "admin"));
        Assert.True(store.Decide(second.Id, AppRequestStatus.Denied, "no", "admin"));

        var whole = store.List(RequestFilter.Everything, ListQuery.All);
        Assert.Equal(["third", "second", "first"], whole.Rows.Select(r => r.Text));

        var slice = store.List(RequestFilter.Everything, new ListQuery(1, 1, null, WantTotal: true));
        Assert.Equal("second", Assert.Single(slice.Rows).Text);
        Assert.True(slice.HasMore);
        Assert.Equal(3, slice.Total);
        Assert.False(store.List(RequestFilter.Everything, new ListQuery(1, 2, null, false)).HasMore);

        Assert.Equal(["first", "second", "third"], store.List(RequestFilter.Everything, ListQuery.All with { Sort = "submitted" }).Rows.Select(r => r.Text));
        Assert.Throws<UnknownSortException>(() => store.List(RequestFilter.Everything, ListQuery.All with { Sort = "text" }));

        Assert.Equal("third", Assert.Single(store.List(RequestFilter.Pending, ListQuery.All).Rows).Text);
        Assert.Equal("first", Assert.Single(store.List(new RequestFilter(AppRequestStatus.Approved), ListQuery.All).Rows).Text);
        Assert.Equal(1, store.List(new RequestFilter(AppRequestStatus.Denied), ListQuery.All with { WantTotal = true }).Total);
    }

    [Fact]
    public void Enrollment_keys_slice_and_sort()
    {
        var store = new EnrollmentKeyStore(_test.Database);
        store.Create("Alpha", EnrollmentEngine.Action1, null, null, "admin");
        store.Create("Charlie", EnrollmentEngine.Agent, null, 5, "admin");
        store.Create("Bravo", EnrollmentEngine.Both, null, null, "admin");

        var whole = store.List(NoFilter.Instance, ListQuery.All);
        Assert.Equal(["Bravo", "Charlie", "Alpha"], whole.Rows.Select(k => k.Name));
        Assert.Equal(whole.Rows.Select(k => k.Id), store.List().Select(k => k.Id));

        var slice = store.List(NoFilter.Instance, new ListQuery(2, 1, null, true));
        Assert.Equal(whole.Rows.Skip(1).Take(2).Select(k => k.Id), slice.Rows.Select(k => k.Id));
        Assert.False(slice.HasMore);
        Assert.Equal(3, slice.Total);

        Assert.Equal(["Alpha", "Bravo", "Charlie"], store.List(NoFilter.Instance, ListQuery.All with { Sort = "name" }).Rows.Select(k => k.Name));
        Assert.Throws<UnknownSortException>(() => store.List(NoFilter.Instance, ListQuery.All with { Sort = "key_hash" }));
    }

    [Fact]
    public void Administrators_slice_and_sort()
    {
        var store = new AdminStore(_test.Database);
        store.Add("carol", TestDatabase.AdminPassword);
        store.Add("alice", TestDatabase.AdminPassword);
        store.Add("bob", TestDatabase.AdminPassword);
        store.SetDisabled("bob", true);

        var whole = store.List(NoFilter.Instance, ListQuery.All);
        Assert.Equal(["alice", "bob", "carol"], whole.Rows.Select(a => a.Username));
        Assert.Equal(whole.Rows.Select(a => a.Id), store.All().Select(a => a.Id));

        var slice = store.List(NoFilter.Instance, new ListQuery(1, 2, null, true));
        Assert.Equal("carol", Assert.Single(slice.Rows).Username);
        Assert.False(slice.HasMore);
        Assert.Equal(3, slice.Total);
        Assert.True(store.List(NoFilter.Instance, new ListQuery(2, 0, null, false)).HasMore);

        Assert.Equal("bob", store.List(NoFilter.Instance, ListQuery.All with { Sort = "-disabled" }).Rows[0].Username);
        Assert.Throws<UnknownSortException>(() => store.List(NoFilter.Instance, ListQuery.All with { Sort = "password_hash" }));
    }

    public void Dispose() => _test.Dispose();
}

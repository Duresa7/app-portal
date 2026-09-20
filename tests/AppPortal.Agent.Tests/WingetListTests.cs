using AppPortal.Agent.Executors;

namespace AppPortal.Agent.Tests;

public sealed class WingetListTests
{
    /// <summary>A transcript of the shape winget prints, header, rule and all.</summary>
    private const string Transcript = """
        Name                           Id                    Version      Available Source
        ---------------------------------------------------------------------------------
        Steam                          Valve.Steam           2.10.91.91             winget
        7-Zip 23.01 (x64)              7zip.7zip             23.01                  winget
        Microsoft Visual C++ 2015 UWP  Microsoft.VCRedist    14.0.33728.0           winget
        """;

    [Fact]
    public void Every_row_becomes_a_name_and_a_version()
    {
        var software = WingetList.Parse(Transcript);

        Assert.Equal(3, software.Count);
        Assert.Equal("Steam", software[0].Name);
        Assert.Equal("2.10.91.91", software[0].Version);
        // A display name with spaces, brackets and a version inside it has to survive whole, which is
        // why the columns are cut at the header's offsets rather than split on whitespace.
        Assert.Equal("7-Zip 23.01 (x64)", software[1].Name);
        Assert.Equal("23.01", software[1].Version);
        Assert.Equal("Microsoft Visual C++ 2015 UWP", software[2].Name);
    }

    [Fact]
    public void The_source_column_does_not_end_up_in_the_version()
    {
        Assert.All(WingetList.Parse(Transcript), item => Assert.DoesNotContain("winget", item.Version));
    }

    [Fact]
    public void A_row_with_no_version_still_reports_its_name()
    {
        var output = """
            Name       Id            Version
            ---------------------------------
            Odd Thing  Vendor.Odd
            """;

        var item = Assert.Single(WingetList.Parse(output));
        Assert.Equal("Odd Thing", item.Name);
        Assert.Equal("", item.Version);
    }

    [Fact]
    public void A_name_winget_had_to_cut_short_is_left_out()
    {
        // Half a name matched against the catalog would claim software the device does not have.
        var output = """
            Name       Id            Version
            ---------------------------------
            A very lo… Vendor.Long   1.0
            """;

        Assert.Empty(WingetList.Parse(output));
    }

    [Theory]
    [InlineData("")]
    [InlineData("No installed package found matching input criteria.")]
    [InlineData("   \n  \n")]
    public void Output_that_is_not_a_table_reads_as_nothing_rather_than_throwing(string output)
    {
        Assert.Empty(WingetList.Parse(output));
    }

    [Fact]
    public void The_spinner_winget_draws_before_the_table_is_not_a_row()
    {
        var output = "\\\n|\n/\n" + Transcript;

        Assert.Equal(3, WingetList.Parse(output).Count);
    }
}

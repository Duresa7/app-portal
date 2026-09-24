using AppPortal.Agent.Jobs;

namespace AppPortal.Agent.Tests;

public sealed class SearchPathTests
{
    private static readonly string[] Frameworks = [@"C:\Apps\VCLibs", @"C:\Apps\Runtime"];

    [Fact]
    public void The_directories_go_in_front_and_the_old_path_stays_behind_them()
    {
        Assert.Equal(@"C:\Apps\VCLibs;C:\Apps\Runtime;C:\Windows\system32;C:\Windows",
            SearchPath.Prepend(Frameworks, @"C:\Windows\system32;C:\Windows"));
    }

    [Fact]
    public void Without_a_path_there_are_only_the_directories()
    {
        Assert.Equal(@"C:\Apps\VCLibs;C:\Apps\Runtime", SearchPath.Prepend(Frameworks, null));
    }

    [Fact]
    public void An_environment_block_has_its_path_rewritten_whatever_its_case()
    {
        // userenv writes Path, and Windows treats the name without regard to case.
        string[] block = [@"=C:=C:\Windows", @"ALLUSERSPROFILE=C:\ProgramData", @"Path=C:\Windows", @"USERNAME=apptester"];

        var rewritten = SearchPath.WithPathFirst(block, Frameworks);

        Assert.Equal<string>(
            [@"=C:=C:\Windows", @"ALLUSERSPROFILE=C:\ProgramData", @"Path=C:\Apps\VCLibs;C:\Apps\Runtime;C:\Windows", @"USERNAME=apptester"],
            rewritten);
    }

    [Fact]
    public void A_drive_entry_is_not_taken_for_the_path()
    {
        // The per-drive entries start with '=', so the first '=' is not where a name ends.
        string[] block = [@"=Path=C:\nowhere", @"PATH=C:\Windows"];

        var rewritten = SearchPath.WithPathFirst(block, Frameworks);

        Assert.Equal<string>([@"=Path=C:\nowhere", @"PATH=C:\Apps\VCLibs;C:\Apps\Runtime;C:\Windows"], rewritten);
    }

    [Fact]
    public void A_block_without_a_path_gets_one()
    {
        var rewritten = SearchPath.WithPathFirst([@"USERNAME=apptester"], Frameworks);

        Assert.Equal<string>([@"USERNAME=apptester", @"Path=C:\Apps\VCLibs;C:\Apps\Runtime"], rewritten);
    }
}

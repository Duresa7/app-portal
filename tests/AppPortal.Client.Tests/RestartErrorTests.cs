using AppPortal.Client.ViewModels;

namespace AppPortal.Client.Tests;

/// <summary>
/// What Restart says when shutdown.exe did not schedule the restart. It used to start the command and
/// never read its answer, so with another account signed in the button did nothing and said nothing.
/// </summary>
public sealed class RestartErrorTests
{
    [Fact]
    public void A_scheduled_restart_says_nothing()
    {
        Assert.Null(AppItemViewModel.RestartError(0));
    }

    [Fact]
    public void Another_person_signed_in_is_named_as_the_reason()
    {
        var error = AppItemViewModel.RestartError(AppItemViewModel.OtherPeopleSignedIn);

        Assert.NotNull(error);
        Assert.Contains("Someone else is signed in", error);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(5)]
    [InlineData(1190)]
    public void Any_other_refusal_asks_for_a_restart_by_hand(int exitCode)
    {
        Assert.Equal("This PC could not be restarted from here. Restart it yourself to finish.", AppItemViewModel.RestartError(exitCode));
    }
}

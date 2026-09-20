using AppPortal.Agent.Executors;

namespace AppPortal.Agent.Tests;

public sealed class WingetOutputTests
{
    [Theory]
    // The bar as winget draws it, with the percentage sitting after the block characters.
    [InlineData("  ██████████████████  100%", 100, "Downloading 100%")]
    [InlineData("  ████████            43.0%", 43, "Downloading 43%")]
    [InlineData("Downloading https://vendor.example/app.exe", null, "Downloading")]
    [InlineData("  1024 KB / 5120 KB  20%", 20, "Downloading 20%")]
    [InlineData("Successfully verified installer hash", null, "Verifying download")]
    [InlineData("Starting package install...", null, "Installing")]
    [InlineData("Found Steam [Valve.Steam] Version 1.0", null, null)]
    [InlineData("", null, null)]
    [InlineData("   ", null, null)]
    public void A_line_of_output_becomes_a_percentage_a_phase_or_nothing(string line, int? percent, string? detail)
    {
        Assert.Equal((percent, detail), WingetOutput.Read(line));
    }

    [Theory]
    [InlineData("150%", 100)]
    [InlineData("-5%", 5)]
    [InlineData("43,5%", 44)]
    public void A_percentage_outside_the_scale_is_brought_back_onto_it(string line, int expected)
    {
        // A bar that reports more than everything, or a locale that writes a decimal comma, must not
        // put a number on the card that a progress control will refuse to draw.
        Assert.Equal(expected, WingetOutput.Read(line).Percent);
    }

    [Fact]
    public void The_tail_is_the_end_of_the_output_and_starts_on_a_whole_line()
    {
        var output = string.Join('\n', Enumerable.Range(1, 200).Select(i => $"line {i} " + new string('x', 40)));

        var tail = WingetOutput.Tail(output, 200);

        Assert.True(tail.Length <= 200);
        Assert.EndsWith("line 200 " + new string('x', 40), tail);
        // Cutting mid-sentence would put half a word at the front of an error message somebody reads.
        Assert.StartsWith("line ", tail);
    }

    [Fact]
    public void Output_shorter_than_the_limit_comes_back_whole()
    {
        Assert.Equal("only this", WingetOutput.Tail("only this\n\n", 4096));
    }
}

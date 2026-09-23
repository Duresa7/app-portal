using System.Diagnostics;

using AppPortal.Agent.Sessions;

namespace AppPortal.Agent.Tests;

public sealed class SessionTranscriptTests
{
    private static readonly TimeSpan ShortGrace = TimeSpan.FromMilliseconds(200);

    [Fact]
    public async Task An_installer_whose_child_holds_the_pipe_returns_within_the_grace_with_the_output_so_far()
    {
        // The installer has exited, but the app it started holds the write end of the pipe, so the
        // read never sees the end of the stream. Waiting for it would keep the install at Installing
        // for as long as somebody keeps the app open.
        var transcript = new SessionTranscript();
        transcript.Append("Installed proof for PC\\person\r\nstarting the app");
        var neverEnds = new TaskCompletionSource();
        var cancelled = 0;
        var clock = Stopwatch.StartNew();

        var output = await transcript.AfterExitAsync(neverEnds.Task, () => cancelled++, ShortGrace);

        clock.Stop();
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"Took {clock.Elapsed}.");
        Assert.Equal(1, cancelled);
        Assert.Contains("Installed proof for PC\\person", output);
        // The last line had no terminator yet, and it is still what the installer said.
        Assert.Contains("starting the app", output);
    }

    [Fact]
    public async Task A_stream_that_ends_in_time_is_read_to_the_end_and_nothing_is_cancelled()
    {
        var transcript = new SessionTranscript();
        var reading = Task.Run(async () =>
        {
            transcript.Append("first\n");
            await Task.Delay(20);
            transcript.Append("second\n");
        });
        var cancelled = 0;

        var output = await transcript.AfterExitAsync(reading, () => cancelled++, TimeSpan.FromSeconds(10));

        Assert.Equal(0, cancelled);
        Assert.Equal(["first", "second"], Lines(output));
    }

    [Fact]
    public async Task A_read_that_does_not_stop_when_cancelled_still_returns()
    {
        // Cancelling the read is a request to the driver, not a guarantee. The install is decided by
        // the exit code, so the transcript is returned whether or not the read ever acknowledges.
        var transcript = new SessionTranscript();
        transcript.Append("partial\n");
        var clock = Stopwatch.StartNew();

        var output = await transcript.AfterExitAsync(new TaskCompletionSource().Task, () => { }, ShortGrace);

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"Took {clock.Elapsed}.");
        Assert.Equal(["partial"], Lines(output));
    }

    [Fact]
    public async Task A_read_that_failed_counts_as_the_end_of_the_stream()
    {
        var transcript = new SessionTranscript();
        transcript.Append("before the pipe broke\n");
        var cancelled = 0;

        var output = await transcript.AfterExitAsync(Task.FromException(new IOException("broken pipe")), () => cancelled++,
            TimeSpan.FromSeconds(10));

        Assert.Equal(0, cancelled);
        Assert.Equal(["before the pipe broke"], Lines(output));
    }

    [Fact]
    public void Lines_break_on_either_terminator_and_empty_lines_are_dropped()
    {
        var seen = new List<string>();
        var transcript = new SessionTranscript(seen.Add);

        transcript.Append("one\r\ntwo\rthr");
        transcript.Append("ee\n\n\nfour");

        Assert.Equal(["one", "two", "three"], seen);
        Assert.Equal(["one", "two", "three", "four"], Lines(transcript.Close()));
        Assert.Equal(["one", "two", "three", "four"], seen);
    }

    [Fact]
    public void Output_after_the_transcript_is_closed_is_dropped()
    {
        // A read the cancel did not stop can still return later. The job has been reported by then,
        // and its log line must not arrive after the result.
        var seen = new List<string>();
        var transcript = new SessionTranscript(seen.Add);
        transcript.Append("kept\n");

        var output = transcript.Close();
        transcript.Append("too late\n");

        Assert.Equal(["kept"], Lines(output));
        Assert.Equal(["kept"], seen);
        Assert.Equal(output, transcript.Close());
    }

    private static string[] Lines(string output)
        => output.ReplaceLineEndings("\n").Split('\n', StringSplitOptions.RemoveEmptyEntries);
}

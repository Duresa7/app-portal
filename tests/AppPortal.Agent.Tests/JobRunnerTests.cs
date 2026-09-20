using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using AppPortal.Agent.Jobs;
using AppPortal.Shared;

using Microsoft.Extensions.Logging.Abstractions;

namespace AppPortal.Agent.Tests;

public sealed class JobRunnerTests
{
    private static readonly PortalSettings Settings = new() { ServerUrl = "https://portal.example", DeviceToken = "apd_test" };
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task A_kind_this_build_cannot_read_is_finished_rather_than_retried()
    {
        AgentJobCompletion? completion = null;
        using var http = new HttpClient(new Handler(async (request, ct) =>
        {
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal(Settings.DeviceToken, request.Headers.Authorization.Parameter);
            if (request.Method == HttpMethod.Get)
            {
                Assert.Equal("?wait=25", request.RequestUri!.Query);
                return Job("unknown");
            }

            Assert.Equal("?attempt=1", request.RequestUri!.Query);
            if (request.RequestUri.AbsolutePath.EndsWith("/complete"))
            {
                completion = await request.Content!.ReadFromJsonAsync<AgentJobCompletion>(ct);
            }

            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }));
        using var runner = Runner(http);
        await runner.RunOnceAsync(Settings, CancellationToken.None);
        Assert.NotNull(completion);
        Assert.False(completion.Ok);
        // Finished, not left to time out. A definition this agent has no type for will never parse, so
        // three more leases would each fail the same way and the person would wait for all of them.
        Assert.Equal("This agent cannot read the package. It is likely older than the server.", completion.Detail);
    }

    [Fact]
    public async Task A_kind_with_no_executor_registered_completes_with_no_executor()
    {
        AgentJobCompletion? completion = null;
        using var http = new HttpClient(new Handler(async (request, ct) =>
        {
            if (request.Method == HttpMethod.Get)
            {
                return Job("winget");
            }

            if (request.RequestUri!.AbsolutePath.EndsWith("/complete"))
            {
                completion = await request.Content!.ReadFromJsonAsync<AgentJobCompletion>(ct);
            }

            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }));

        // The definition reads perfectly well; this build simply carries no executor for it yet.
        using var runner = Runner(http);
        await runner.RunOnceAsync(Settings, CancellationToken.None);

        Assert.NotNull(completion);
        Assert.False(completion.Ok);
        Assert.Equal("no executor", completion.Detail);
    }

    [Fact]
    public async Task Progress_reaches_the_server_before_completion_and_quiet_jobs_renew_the_lease()
    {
        var reports = new List<AgentJobProgress>();
        var renewed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = false;
        using var http = new HttpClient(new Handler(async (request, ct) =>
        {
            if (request.Method == HttpMethod.Get)
            {
                return Job("direct");
            }

            if (request.RequestUri!.AbsolutePath.EndsWith("/progress"))
            {
                var progress = (await request.Content!.ReadFromJsonAsync<AgentJobProgress>(ct))!;
                reports.Add(progress);
                if (reports.Count(p => p.Percent == 43) >= 2)
                {
                    renewed.TrySetResult();
                }
            }
            else
            {
                completed = true;
                Assert.Equal("Verifying download", reports.Last().Detail);
            }

            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }));
        var executor = new Executor(async (progress, ct) =>
        {
            progress.Report((43, "Downloading 43%"));
            await renewed.Task.WaitAsync(TimeSpan.FromSeconds(3), ct);
            progress.Report((100, "Verifying download"));
            return new ExecutionResult(true, "Installed", 0);
        });
        using var runner = Runner(http, executor);
        await runner.RunOnceAsync(Settings, CancellationToken.None);
        Assert.True(completed);
        Assert.Contains(reports, p => p is { State: "downloading", Percent: 43, Detail: "Downloading 43%" });
    }

    [Fact]
    public async Task Stopping_cancels_execution_and_returns_the_job_with_an_independent_token()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var returned = false;
        using var http = new HttpClient(new Handler(async (request, ct) =>
        {
            if (request.Method == HttpMethod.Get)
            {
                return Job("direct");
            }

            Assert.EndsWith("/progress", request.RequestUri!.AbsolutePath);
            var progress = (await request.Content!.ReadFromJsonAsync<AgentJobProgress>(ct))!;
            if (progress.State == "queued")
            {
                Assert.False(ct.IsCancellationRequested);
                returned = true;
            }

            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }));
        var executor = new Executor(async (_, ct) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return new ExecutionResult(true, null);
        });
        using var runner = Runner(http, executor);
        await runner.StartAsync(CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await runner.StopAsync(deadline.Token);
        Assert.True(returned);
        Assert.True(runner.ExecuteTask!.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task Failed_progress_cancels_the_executor_instead_of_running_with_a_lost_lease()
    {
        var cancelled = false;
        var calls = 0;
        using var http = new HttpClient(new Handler((request, _) =>
        {
            if (request.Method == HttpMethod.Get)
            {
                return Task.FromResult(Job("direct"));
            }

            return Task.FromResult(new HttpResponseMessage(++calls == 1 ? HttpStatusCode.Conflict : HttpStatusCode.NoContent));
        }));
        var executor = new Executor(async (_, ct) =>
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
                throw;
            }

            return new ExecutionResult(true, null);
        });
        using var runner = Runner(http, executor);
        await Assert.ThrowsAsync<HttpRequestException>(() => runner.RunOnceAsync(Settings, CancellationToken.None));
        Assert.True(cancelled);
    }

    [Fact]
    public async Task Executor_failure_is_reported_and_an_empty_poll_runs_no_executor()
    {
        var calls = 0;
        AgentJobCompletion? completion = null;
        using var http = new HttpClient(new Handler(async (request, ct) =>
        {
            if (request.Method == HttpMethod.Get)
            {
                return ++calls == 1 ? new HttpResponseMessage(HttpStatusCode.NoContent) : Job("direct");
            }

            if (request.RequestUri!.AbsolutePath.EndsWith("/complete"))
            {
                completion = await request.Content!.ReadFromJsonAsync<AgentJobCompletion>(ct);
            }

            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }));
        using var runner = Runner(http, new Executor((_, _) => throw new IOException("private path")));
        await runner.RunOnceAsync(Settings, CancellationToken.None);
        Assert.Null(completion);
        await runner.RunOnceAsync(Settings, CancellationToken.None);
        Assert.False(completion!.Ok);
        Assert.Equal("Executor failed (IOException).", completion.Detail);
    }

    // Raw JSON rather than a typed AgentJob, so that a kind this build has no type for can be sent at
    // all. That is the case the runner has to survive: a server newer than the agent.
    private static HttpResponseMessage Job(string kind) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(
            "{\"id\":\"job-1\",\"installId\":\"install-1\",\"attempt\":1,\"definition\":" + Definition(kind) + "}",
            System.Text.Encoding.UTF8,
            "application/json"),
    };

    private static string Definition(string kind) => kind switch
    {
        "direct" => "{\"kind\":\"direct\",\"url\":\"https://vendor.example/app.exe\",\"sha256\":\""
                    + new string('a', 64)
                    + "\",\"installerType\":\"exe\",\"silentArgs\":\"/S\",\"sizeBytes\":1000,"
                    + "\"uninstallKey\":null,\"scope\":\"machine\",\"requiresReboot\":false}",
        "winget" => "{\"kind\":\"winget\",\"id\":\"Vendor.App\",\"scope\":\"machine\"}",
        _ => "{\"kind\":\"" + kind + "\"}",
    };

    private static JobRunner Runner(HttpClient http, params IPackageExecutor[] executors)
        => new(http, executors, NullLogger<JobRunner>.Instance, () => Settings, TimeSpan.FromMilliseconds(30));

    private sealed class Executor(Func<IProgress<(int percent, string detail)>, CancellationToken, Task<ExecutionResult>> run) : IPackageExecutor
    {
        public string Kind => "direct";
        public Task<ExecutionResult> RunAsync(PackageDefinition d, IProgress<(int percent, string detail)> p, CancellationToken ct) => run(p, ct);
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handle) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => handle(request, cancellationToken);
    }
}

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Threading.Channels;

using AppPortal.Shared;

namespace AppPortal.Agent.Jobs;

public sealed class JobRunner(
    HttpClient http,
    IEnumerable<IPackageExecutor> executors,
    ILogger<JobRunner> logger,
    Func<PortalSettings>? loadSettings = null,
    TimeSpan? renewalInterval = null) : BackgroundService
{
    private readonly Dictionary<string, IPackageExecutor> _executors = executors.ToDictionary(e => e.Kind, StringComparer.OrdinalIgnoreCase);
    private readonly StubExecutor _fallback = new();
    private readonly TimeSpan _renewalInterval = renewalInterval ?? TimeSpan.FromSeconds(60);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var settings = (loadSettings ?? (() => PortalSettings.Load()))();
                await RunOnceAsync(settings, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning("Job request failed ({Reason}); will retry", ex.GetType().Name);
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
            }
        }
    }

    public async Task RunOnceAsync(PortalSettings settings, CancellationToken ct)
    {
        using var response = await SendAsync(settings, HttpMethod.Get, "jobs?wait=25", null, ct);
        if (response.StatusCode == HttpStatusCode.NoContent)
        {
            return;
        }

        var job = await response.Content.ReadFromJsonAsync<AgentJob>(ct)
                  ?? throw new InvalidDataException("The job response was empty.");
        var route = $"jobs/{Uri.EscapeDataString(job.Id)}";
        var attempt = $"?attempt={job.Attempt}";
        using var running = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var progress = new JobProgress();
        var reporting = ReportAsync(settings, route + "/progress" + attempt, progress, running.Token);
        Task<ExecutionResult>? execution = null;
        try
        {
            var executor = _executors.GetValueOrDefault(job.Definition.Kind) ?? _fallback;
            execution = ExecuteAsync(executor, job.Definition, progress, running.Token);
            var first = await Task.WhenAny(execution, reporting);
            if (first == reporting)
            {
                await reporting;
            }

            var result = await execution;
            progress.Updates.Writer.TryComplete();
            await reporting;
            using var completed = await SendAsync(settings, HttpMethod.Post, route + "/complete" + attempt,
                new AgentJobCompletion(result.Ok, result.Detail, result.ExitCode), ct);
        }
        catch
        {
            await running.CancelAsync();
            try
            {
                if (execution is not null)
                {
                    await execution;
                }
            }
            catch (OperationCanceledException) when (running.IsCancellationRequested)
            {
            }

            // The service token is already cancelled on stop; this short separate request returns the
            // lease so a restart can resume immediately. A disconnected server falls back to expiry.
            using var release = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                using var returned = await SendAsync(settings, HttpMethod.Post, route + "/progress" + attempt,
                    new AgentJobProgress("queued", 0, "Waiting for the agent to resume."), release.Token);
            }
            catch (Exception ex)
            {
                logger.LogWarning("Could not return job {Id} ({Reason}); its lease will expire", job.Id, ex.GetType().Name);
            }

            throw;
        }
        finally
        {
            await running.CancelAsync();
            try
            {
                await reporting;
            }
            catch (Exception) when (running.IsCancellationRequested)
            {
            }
        }
    }

    private static async Task<ExecutionResult> ExecuteAsync(IPackageExecutor executor, PackageDefinition definition,
        IProgress<(int percent, string detail)> progress, CancellationToken ct)
    {
        try
        {
            return await executor.RunAsync(definition, progress, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new ExecutionResult(false, $"Executor failed ({ex.GetType().Name}).");
        }
    }

    private async Task ReportAsync(PortalSettings settings, string route, JobProgress progress, CancellationToken ct)
    {
        var current = new AgentJobProgress("downloading", 0, "Downloading 0%");
        while (true)
        {
            using var response = await SendAsync(settings, HttpMethod.Post, route, current, ct);
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
            wait.CancelAfter(_renewalInterval);
            try
            {
                if (!await progress.Updates.Reader.WaitToReadAsync(wait.Token))
                {
                    return;
                }

                while (progress.Updates.Reader.TryRead(out var update))
                {
                    var state = update.detail.StartsWith("Downloading", StringComparison.OrdinalIgnoreCase)
                                || update.detail == "Verifying download" ? "downloading" : "installing";
                    current = new AgentJobProgress(state, Math.Clamp(update.percent, 0, 100), update.detail);
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Quiet installers still need a lease renewal; their last progress remains useful.
            }
        }
    }

    private async Task<HttpResponseMessage> SendAsync(PortalSettings settings, HttpMethod method, string route, object? body, CancellationToken ct)
    {
        if (!settings.IsConfigured)
        {
            throw new InvalidOperationException("The server URL and device token must be configured in client.json.");
        }

        using var request = new HttpRequestMessage(method, new Uri(new Uri(settings.ServerUrl.TrimEnd('/') + "/"), "api/v1/agent/" + route));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.DeviceToken);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            response.Dispose();
            response.EnsureSuccessStatusCode();
        }

        return response;
    }

    private sealed class JobProgress : IProgress<(int percent, string detail)>
    {
        public Channel<(int percent, string detail)> Updates { get; } = Channel.CreateBounded<(int, string)>(
            new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });

        public void Report((int percent, string detail) value) => Updates.Writer.TryWrite(value);
    }
}

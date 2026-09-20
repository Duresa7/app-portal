using System.Diagnostics;
using System.ServiceProcess;

namespace AppPortal.Agent;

/// <summary>
/// Registers and removes the Windows service. The MSI does this with its own ServiceInstall element
/// rather than calling in here; these flags exist so the agent can be run and torn down by hand on a
/// developer machine without one.
/// </summary>
public static class ServiceRegistration
{
    public const string ServiceName = "AppPortalAgent";

    public static async Task<int> RunAsync(bool install)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("Service registration is only available on Windows.");
            return 1;
        }

        try
        {
            if (!install)
            {
                using var service = new ServiceController(ServiceName);
                if (service.Status != ServiceControllerStatus.Stopped)
                {
                    if (service.Status != ServiceControllerStatus.StopPending)
                    {
                        service.Stop();
                    }

                    service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(60));
                }

                return await ScAsync("delete", ServiceName);
            }

            var executable = Path.Combine(AppContext.BaseDirectory, "AppPortal.Agent.exe");
            if (!File.Exists(executable))
            {
                Console.Error.WriteLine("Publish the Windows agent before installing the service.");
                return 1;
            }

            var result = await ScAsync("create", ServiceName, "binPath=", $"\"{executable}\"", "start=", "delayed-auto",
                "obj=", "LocalSystem", "DisplayName=", "App Portal Agent");
            if (result != 0)
            {
                return result;
            }

            result = await ScAsync("failure", ServiceName, "reset=", "86400", "actions=", "restart/60000");
            if (result != 0)
            {
                return result;
            }

            return await ScAsync("failureflag", ServiceName, "1");
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or System.ServiceProcess.TimeoutException)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static async Task<int> ScAsync(params string[] args)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "sc.exe")) { UseShellExecute = false };
        foreach (var argument in args)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start sc.exe.");
        await process.WaitForExitAsync();
        return process.ExitCode;
    }
}

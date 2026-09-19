using AppPortal.Server.Devices;

namespace AppPortal.Server.Cli;

/// <summary>
/// `device add|list|remove` administration, run against the same devices file the server reads.
/// The plaintext token is printed exactly once; store it in a password manager, not in a file in this repository.
/// </summary>
public static class DeviceCli
{
    public static int Run(string[] args, DeviceStore store, TextWriter output)
    {
        if (args.Length < 2 || args[0] != "device")
        {
            return Usage(output);
        }

        switch (args[1])
        {
            case "add":
                {
                    var name = Option(args, "--name");
                    var endpointId = Option(args, "--endpoint-id");
                    if (name is null || endpointId is null)
                    {
                        return Usage(output);
                    }

                    var token = store.Add(name, endpointId);
                    output.WriteLine($"Device '{name}' registered for endpoint {endpointId}.");
                    output.WriteLine("Device token (shown once):");
                    output.WriteLine(token);
                    return 0;
                }

            case "list":
                foreach (var device in store.All())
                {
                    output.WriteLine($"{device.Name}\t{device.EndpointId}\t{(device.Enabled ? "enabled" : "disabled")}\t{device.CreatedAt:u}");
                }

                return 0;

            case "remove":
                {
                    var name = Option(args, "--name");
                    if (name is null)
                    {
                        return Usage(output);
                    }

                    output.WriteLine(store.Remove(name) ? $"Removed '{name}'." : $"No device named '{name}'.");
                    return 0;
                }

            default:
                return Usage(output);
        }
    }

    private static string? Option(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static int Usage(TextWriter output)
    {
        output.WriteLine("Usage:");
        output.WriteLine("  AppPortal.Server device add --name <device-name> --endpoint-id <action1-endpoint-id>");
        output.WriteLine("  AppPortal.Server device list");
        output.WriteLine("  AppPortal.Server device remove --name <device-name>");
        return 2;
    }
}

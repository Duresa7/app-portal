using System.Text.Json;

using AppPortal.Server.Action1;
using AppPortal.Server.Catalog;
using AppPortal.Shared;

namespace AppPortal.Server.Cli;

/// <summary>
/// Import and export share the seed file format, so operators can move the catalog between deployments.
/// Verification checks definitions before devices rely on them; winget lookup remains best effort.
/// </summary>
public static class CatalogCli
{
    public static async Task<int> RunAsync(string[] args, CatalogStore catalog, IAction1Client action1, TextWriter output, CancellationToken ct, PackageHelpers? helpers = null)
    {
        if (args.Length >= 2 && args[0] == "catalog" && args[1] == "verify")
        {
            var failures = 0;
            IReadOnlyList<CatalogEntry> entries;
            try
            {
                entries = catalog.Entries;
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException)
            {
                output.WriteLine($"ERROR    Invalid package definition: {ex.Message}");
                return 1;
            }

            foreach (var entry in entries)
            {
                if (entry.Agent is { } agent)
                {
                    try
                    {
                        agent.Validate();
                        output.WriteLine($"OK       {entry.Id}: agent definition is valid.");
                        if (agent is WingetPackageDefinition winget)
                        {
                            var lookup = await (helpers ?? PackageHelpers.Shared)
                                .LookupWingetAsync(winget.Id, ct, winget.Source);
                            output.WriteLine($"INFO     {entry.Id}: {lookup.Message}");
                        }
                    }
                    catch (InvalidDataException ex)
                    {
                        failures++;
                        output.WriteLine($"ERROR    {entry.Id}: {ex.Message}");
                    }
                }

                if (!entry.HasAction1)
                {
                    continue;
                }

                try
                {
                    var version = await action1.ResolvePackageVersionAsync(entry.Action1.PackageId, entry.Action1.Version, ct);
                    if (version is null)
                    {
                        failures++;
                        output.WriteLine($"MISSING  {entry.Id}: no published version '{entry.Action1.Version}' for package {entry.Action1.PackageId}");
                    }
                    else
                    {
                        output.WriteLine($"OK       {entry.Id}: {entry.Action1.PackageId} -> {version.Version}");
                    }
                }
                catch (Action1Exception ex)
                {
                    failures++;
                    output.WriteLine($"ERROR    {entry.Id}: {ex.Message}");
                }
            }

            output.WriteLine(failures == 0 ? "All catalog packages resolve." : $"{failures} catalog package(s) did not resolve.");
            return failures == 0 ? 0 : 1;
        }

        if (args.Length >= 3 && args[0] == "catalog" && args[1] == "import")
        {
            var path = args[2];
            if (!File.Exists(path))
            {
                output.WriteLine($"No such file: {path}");
                return 1;
            }

            try
            {
                var entries = CatalogStore.Parse(await File.ReadAllTextAsync(path, ct));
                catalog.Import(entries);
                output.WriteLine($"Imported {entries.Count} app(s) from {path}.");
                return 0;
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException or PrerequisiteException)
            {
                output.WriteLine($"{path} is not a valid catalog: {ex.Message}");
                return 1;
            }
        }

        if (args.Length >= 2 && args[0] == "catalog" && args[1] == "export")
        {
            output.WriteLine(catalog.ExportJson());
            return 0;
        }

        if (args.Length >= 3 && args[0] == "packages" && args[1] == "search")
        {
            var filter = string.Join(' ', args.Skip(2));
            var packages = await action1.SearchPackagesAsync(filter, ct);
            if (packages.Count == 0)
            {
                output.WriteLine("No packages matched.");
                return 1;
            }

            foreach (var package in packages)
            {
                output.WriteLine($"{package.Id}\t{package.Name}\t{package.Vendor}\t{(package.Builtin ? "builtin" : "custom")}");
            }

            return 0;
        }

        output.WriteLine("Usage:");
        output.WriteLine("  AppPortal.Server catalog verify");
        output.WriteLine("  AppPortal.Server catalog import <file>");
        output.WriteLine("  AppPortal.Server catalog export");
        output.WriteLine("  AppPortal.Server packages search <name>");
        return 2;
    }
}

using AppPortal.Server.Action1;
using AppPortal.Server.Catalog;

namespace AppPortal.Server.Cli;

/// <summary>`catalog verify` checks every catalog package against the Software Repository; `packages search` finds IDs.</summary>
public static class CatalogCli
{
    public static async Task<int> RunAsync(string[] args, CatalogStore catalog, IAction1Client action1, TextWriter output, CancellationToken ct)
    {
        if (args.Length >= 2 && args[0] == "catalog" && args[1] == "verify")
        {
            var failures = 0;
            foreach (var entry in catalog.Entries)
            {
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
        output.WriteLine("  AppPortal.Server packages search <name>");
        return 2;
    }
}

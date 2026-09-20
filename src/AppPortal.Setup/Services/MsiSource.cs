using System;
using System.IO;
using System.Reflection;

namespace AppPortal.Setup.Services;

/// <summary>
/// Where the package comes from. One implementation unpacks it from inside this executable; a test
/// hands over a file it made itself, which is what keeps the install path runnable off Windows.
/// </summary>
public interface IMsiSource
{
    /// <summary>True when there is a package to install at all.</summary>
    bool Present { get; }

    /// <summary>Writes the package into <paramref name="directory"/> and gives back the file it wrote.</summary>
    string Extract(string directory);
}

/// <summary>
/// The MSI carried inside this executable. The build embeds it when one exists and leaves it out when
/// one does not, so a build made without a package still runs and says so instead of failing to compile.
/// </summary>
public sealed class EmbeddedMsiSource(Assembly assembly, string resourceName) : IMsiSource
{
    /// <summary>The logical name the project file gives the embedded package.</summary>
    public const string ResourceName = "AppPortal.msi";

    /// <summary>The name the package is written under, and the name msiexec logs.</summary>
    public const string FileName = "AppPortal.msi";

    public const string MissingMessage =
        "This AppPortalSetup.exe was built without the App Portal installer inside it. "
        + "Use the AppPortalSetup.exe from the release, or rebuild with -p:AppPortalMsiPath=<path to the .msi>.";

    public EmbeddedMsiSource()
        : this(typeof(EmbeddedMsiSource).Assembly, ResourceName)
    {
    }

    public bool Present => assembly.GetManifestResourceInfo(resourceName) is not null;

    public string Extract(string directory)
    {
        using var packaged = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(MissingMessage);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, FileName);
        using (var file = File.Create(path))
        {
            packaged.CopyTo(file);
        }

        return path;
    }
}

using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;

using AppPortal.Client.ViewModels.Admin;
using AppPortal.Shared;

using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace AppPortal.Client.Views.Admin;

/// <summary>
/// The catalog page. It is also the page's <see cref="ICatalogFiles"/>, because the file pickers belong
/// to the window this view is in and the view model has no window.
/// </summary>
public partial class CatalogView : UserControl, ICatalogFiles
{
    private static readonly FilePickerFileType CatalogFile = new("Catalog file")
    {
        Patterns = ["*.json"],
        MimeTypes = ["application/json"],
    };

    public CatalogView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is CatalogViewModel page)
            {
                page.Files = this;
            }
        };
    }

    public async Task<string?> OpenAsync()
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { CanOpen: true } storage)
        {
            return null;
        }

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import a catalog file",
            AllowMultiple = false,
            FileTypeFilter = [CatalogFile, FilePickerFileTypes.All],
        });
        if (files.Count == 0)
        {
            return null;
        }

        // One character past the limit and no further, the same as the server reads an upload: enough
        // to tell the file is too big, without holding all of a file somebody picked by mistake.
        await using var stream = await files[0].OpenReadAsync();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var buffer = new char[AdminApiLimits.MaxImportBytes + 1];
        var read = await reader.ReadBlockAsync(buffer);
        return new string(buffer, 0, read);
    }

    public async Task<string?> SaveAsync(string json)
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { CanSave: true } storage)
        {
            return null;
        }

        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export the catalog",
            SuggestedFileName = "catalog.json",
            DefaultExtension = "json",
            FileTypeChoices = [CatalogFile],
            ShowOverwritePrompt = true,
        });
        if (file is null)
        {
            return null;
        }

        await using var stream = await file.OpenWriteAsync();
        // Truncated first: writing a shorter catalog over a longer file would leave the old tail behind.
        stream.SetLength(0);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        await writer.WriteAsync(json);
        return file.Name;
    }
}

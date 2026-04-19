using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace Vomplayer.Services;

public sealed class FilePickerTopLevel : IFilePicker
{
    private readonly TopLevel topLevel;

    public FilePickerTopLevel(TopLevel topLevel)
    {
        if (topLevel == null)
        {
            throw new ArgumentNullException(nameof(topLevel));
        }
        this.topLevel = topLevel;
    }

    public async Task<string?> PickVideoFileAsync(string title)
    {
        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
        });
        if (files.Count == 0)
        {
            return null;
        }
        return files[0].TryGetLocalPath();
    }
}

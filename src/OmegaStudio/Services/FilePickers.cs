using Avalonia.Platform.Storage;
namespace OmegaStudio.Services;

internal static class FilePickers
{
    public static async Task<string?> SaveAsync(
        IAppStorageProvider storage, string title, string suggestedName, string extension, string typeLabel)
    {
        string bare = extension.TrimStart('.');
        IStorageFile? file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedName,
            DefaultExtension = bare,
            FileTypeChoices = [new FilePickerFileType(typeLabel) { Patterns = [$"*.{bare}"] }],
        });
        return file?.TryGetLocalPath();
    }
    public static async Task<string?> OpenAsync(
        IAppStorageProvider storage, string title, string typeLabel, params string[] patterns)
    {
        IReadOnlyList<IStorageFile> files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType(typeLabel) { Patterns = patterns }],
        });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }
}

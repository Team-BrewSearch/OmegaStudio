using Avalonia.Platform.Storage;
using Omega.Plugins;
namespace OmegaStudio.Services;
internal static class FilePickers
{
    public static async Task<string?> SaveAsync(
        IAppStorageProvider storage, string title, string suggestedName, IReadOnlyList<FileFilter> filters)
    {
        IStorageFile? file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedName,
            DefaultExtension = filters.Count > 0 ? filters[0].PrimaryExtension.TrimStart('.') : null,
            FileTypeChoices = Choices(filters),
        });
        return file?.TryGetLocalPath();
    }
    public static async Task<string?> OpenAsync(
        IAppStorageProvider storage, string title, IReadOnlyList<FileFilter> filters)
    {
        IReadOnlyList<IStorageFile> files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = Choices(filters),
        });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }
    private static FilePickerFileType[] Choices(IReadOnlyList<FileFilter> filters)
    {
        return [.. filters.Select(
            filter => new FilePickerFileType(filter.Label) { Patterns = [.. filter.Patterns] })];
    }
}

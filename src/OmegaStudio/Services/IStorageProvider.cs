using Avalonia.Platform.Storage;
namespace OmegaStudio.Services;
public interface IAppStorageProvider
{
    Task<IReadOnlyList<IStorageFile>> OpenFilePickerAsync(FilePickerOpenOptions options);
    Task<IStorageFile?> SaveFilePickerAsync(FilePickerSaveOptions options);
}
internal sealed class WindowStorageProvider(IStorageProvider inner) : IAppStorageProvider
{
    public Task<IReadOnlyList<IStorageFile>> OpenFilePickerAsync(FilePickerOpenOptions options)
    {
        return inner.OpenFilePickerAsync(options);
    }
    public Task<IStorageFile?> SaveFilePickerAsync(FilePickerSaveOptions options)
    {
        return inner.SaveFilePickerAsync(options);
    }
}

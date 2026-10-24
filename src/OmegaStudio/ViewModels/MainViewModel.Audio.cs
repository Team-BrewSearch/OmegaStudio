using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using Omega.Plugins;
using OmegaStudio.Audio;
using OmegaStudio.Plugins;
namespace OmegaStudio.ViewModels;
public partial class MainViewModel
{
    private bool _pushingAudioPosition;
    private bool _isDraggingThumb;
    internal void SetThumbHeld(bool held)
    {
        _isDraggingThumb = held;
    }
    public string AudioStatus => _audioInfo?.ToString() ?? "No audio selected";
    public bool AudioIsLoaded => _audioPlayer.IsLoaded;
    public bool AudioCanPlay => _audioPlayer.CanPlay;
    public bool AudioCanPauseOrResume => _audioPlayer.CanPause || _audioPlayer.CanResume;
    public bool AudioCanStop => _audioPlayer.CanStop;
    public double AudioTotalSeconds => _audioPlayer.Duration.TotalSeconds;
    public string AudioTimeDisplay =>
        $"{_audioPlayer.Position:mm\\:ss\\.fff} / {_audioPlayer.Duration:mm\\:ss\\.fff}";
    public double AudioPositionSeconds
    {
        get;
        set
        {
            if (field.Equals(value))
            {
                return;
            }
            field = value;
            OnPropertyChanged();
            if (!_pushingAudioPosition)
            {
                _audioPlayer.SeekTo(TimeSpan.FromSeconds(value));
            }
        }
    }
    [RelayCommand]
    private async Task PlayAudioAsync()
    {
        if (SelectedItem?.Node is not IAudioAsset audio)
        {
            return;
        }
        bool installed = await LoadAudioAsync(audio, null);
        if (installed && ReferenceEquals(SelectedItem?.Node, audio) && ReferenceEquals(_currentAudio, audio))
        {
            _audioPlayer.Play();
        }
    }
    [RelayCommand]
    private void PauseResumeAudio()
    {
        _audioPlayer.TogglePause();
    }
    [RelayCommand]
    private void StopAudio()
    {
        _audioPlayer.Stop();
    }
    private int _audioGeneration;
    private readonly SemaphoreSlim _audioGate = new(1, 1);
    private Task<bool>? _audioLoad;
    public Task<bool> LoadAudioAsync(IAudioAsset audio, byte[]? data)
    {
        if (_audioLoad is not null && ReferenceEquals(audio, _currentAudio))
        {
            return _audioLoad;
        }
        _currentAudio = audio;
        _audioLoad = InstallAudioAsync(++_audioGeneration, data is null ? () => NodeAccess.EncodedAudio(audio).ToArray() : () => data);
        return _audioLoad;
    }
    private Task<bool> LoadSoundtrackAsync(byte[] data)
    {
        _currentAudio = null;
        _audioLoad = null;
        return InstallAudioAsync(++_audioGeneration, () => data);
    }
    private async Task<bool> InstallAudioAsync(int generation, Func<byte[]> data)
    {
        await _audioGate.WaitAsync();
        try
        {
            if (generation != _audioGeneration || _disposed)
            {
                return false;
            }
            using PreparedAudio prepared = await Task.Run(() => AudioPlaybackService.Prepare(data()));
            if (generation != _audioGeneration || _disposed)
            {
                return false;
            }
            _audioPlayer.Commit(prepared);
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (generation == _audioGeneration)
            {
                _currentAudio = null;
                _audioLoad = null;
                Log($"Failed to load audio: {ex.GetType().Name}: {ex.Message}");
            }
            return false;
        }
        finally
        {
            _audioGate.Release();
            NotifyAudioState();
        }
    }
    public void ResetAudioState()
    {
        _audioGeneration++;
        _currentAudio = null;
        _audioLoad = null;
        _audioPlayer.Clear();
        NotifyAudioState();
    }
    private void NotifyAudioState()
    {
        OnPropertyChanged(nameof(AudioStatus));
        OnPropertyChanged(nameof(AudioIsLoaded));
        OnPropertyChanged(nameof(AudioCanPlay));
        OnPropertyChanged(nameof(AudioCanPauseOrResume));
        OnPropertyChanged(nameof(AudioCanStop));
        _pushingAudioPosition = true;
        try
        {
            OnPropertyChanged(nameof(AudioTotalSeconds));
            if (!_isDraggingThumb)
            {
                AudioPositionSeconds = _audioPlayer.Position.TotalSeconds;
            }
        }
        finally
        {
            _pushingAudioPosition = false;
        }
        OnPropertyChanged(nameof(AudioTimeDisplay));
    }
    private void OnAudioProgress()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(OnAudioProgress);
            return;
        }
        _pushingAudioPosition = true;
        try
        {
            if (!_isDraggingThumb)
            {
                AudioPositionSeconds = _audioPlayer.Position.TotalSeconds;
            }
        }
        finally
        {
            _pushingAudioPosition = false;
        }
        OnPropertyChanged(nameof(AudioTimeDisplay));
    }
    private void OnAudioPlayerStateChanged()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(OnAudioPlayerStateChanged);
            return;
        }
        NotifyAudioState();
    }
}

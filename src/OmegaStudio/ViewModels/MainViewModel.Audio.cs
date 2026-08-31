using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using Omega.Plugins;
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
        await LoadAudioAsync(audio);
        _audioPlayer.Play();
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
    private Task LoadAudioAsync(IAudioAsset audio)
    {
        if (_audioLoad is not null && ReferenceEquals(audio, _currentAudio))
        {
            return _audioLoad;
        }
        _currentAudio = audio;
        _audioLoad = RunAudioLoadAsync(audio);
        return _audioLoad;
    }
    private async Task RunAudioLoadAsync(IAudioAsset audio)
    {
        try
        {
            byte[] data = audio.EncodedAudio.ToArray();
            await Task.Run(() => _audioPlayer.Load(data));
        }
        catch (Exception ex)
        {
            _currentAudio = null;
            _audioLoad = null;
            Log($"Failed to load audio: {ex.Message}");
        }
        NotifyAudioState();
    }
    private void ResetAudioState()
    {
        if (_audioPlayer.IsLoaded)
        {
            _audioPlayer.Stop();
        }
        _currentAudio = null;
        _audioLoad = null;
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

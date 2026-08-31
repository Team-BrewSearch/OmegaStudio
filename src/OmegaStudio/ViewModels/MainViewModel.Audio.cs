using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using Omega.Plugins;

namespace OmegaStudio.ViewModels;

/// <summary>The Audio pane's transport surface.</summary>
public partial class MainViewModel
{
    private bool _pushingAudioPosition;
    private bool _isDraggingThumb;

    /// <summary>
    /// Called by the view when the user takes hold of the seek slider's thumb, and
    /// again when they let go. While the thumb is held the poll timer stops writing
    /// the live position into the slider, so the thumb tracks the pointer exactly
    /// instead of creeping forward under it.
    /// </summary>
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

    /// <summary>
    /// Where the transport slider sits, and the only way to scrub.
    ///
    /// <para>
    /// Two-way bound, which is what makes every way of moving a slider work — thumb
    /// drag, click on the track, arrow and page keys, touch — without the view
    /// knowing about any of them. The danger of a two-way binding here is the echo:
    /// the poll timer writes the live position 40 times a second, and if each of
    /// those writes came back as a seek the transport would spend its life seeking
    /// to where it already was. <see cref="_pushingAudioPosition"/> marks the writes
    /// this class made, and only unmarked ones — the user's — reach the player.
    /// </para>
    /// </summary>
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

    /// <summary>
    /// Decodes a clip into the player, at most once per clip.
    ///
    /// <para>
    /// Selecting an audio node starts a load, and pressing Play before that load
    /// finished used to start a second one for the same clip. Both reached
    /// <c>Load</c>, which unloads before it decodes — so the pair of them left
    /// <c>Duration</c> at zero for as long as the second decode took, which the
    /// transport slider sees as a zero-length clip. Handing back the load already in
    /// flight leaves exactly one decode per clip.
    /// </para>
    /// </summary>
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
            // Copied on the UI thread: EncodedAudio is a span over memory the plugin
            // owns, and it cannot be captured into the decode running off-thread.
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

    /// <summary>
    /// Raises every audio-derived property at once. They all read through the
    /// player, so they always change together — four separate copies of this list
    /// used to drift apart.
    /// </summary>
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
            // Raising the slider's Maximum makes it clamp its Value back into range
            // and write the clamped number here. Loading a shorter clip than the last
            // one would otherwise arrive as a seek the user never asked for.
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
        // The poll timer runs on the thread pool.
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(OnAudioPlayerStateChanged);
            return;
        }

        NotifyAudioState();
    }
}

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using Microsoft.Win32;
using Mp3ClipEditorTagger.Controls;
using Mp3ClipEditorTagger.Models;
using Mp3ClipEditorTagger.Services;

namespace Mp3ClipEditorTagger;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private readonly FfmpegService _ffmpegService = new();
    private readonly AudioPreviewService _audioPreviewService = new();
    private readonly FilenameMetadataService _filenameMetadataService = new();
    private readonly DispatcherTimer _playbackTimer;

    private TrackItem? _selectedTrack;
    private string _loadStatus = "Idle";
    private int _exportProgressPercent;
    private double? _playheadRatio;
    private double _defaultClipSeconds = 45;
    private double _defaultFadeInSeconds = 0;
    private double _defaultFadeOutSeconds = 0;
    private string _currentPositionText = "0:00";
    private int _selectionLoadVersion;
    private bool _suppressPlaybackStopForSelectionChange;
    private bool _isSelectionPreviewPlayback;
    private bool _applyPreviewFadeIn;
    private bool _applyPreviewFadeOut;
    private double _previewFadeInStartSeconds;
    private string _renameStatusText = "Ready to parse and rename queued MP3 files.";
    private RenamePatternOption _selectedRenamePattern = FilenameMetadataService.PatternOptions[0];
    private bool _isRenamerExpanded;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;

        _playbackTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(120)
        };
        _playbackTimer.Tick += PlaybackTimer_Tick;
        _audioPreviewService.PlaybackStopped += AudioPreviewService_PlaybackStopped;

        try
        {
            _ffmpegService.EnsureAvailable();
            LoadStatus = "Ready";
        }
        catch (Exception error)
        {
            LoadStatus = "FFmpeg missing";
            MessageBox.Show(this, error.Message, "Bundled FFmpeg Not Found", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<TrackItem> Tracks { get; } = [];

    public IReadOnlyList<RenamePatternOption> RenamePatternOptions => FilenameMetadataService.PatternOptions;

    public TrackItem? SelectedTrack
    {
        get => _selectedTrack;
        set
        {
            if (ReferenceEquals(_selectedTrack, value))
            {
                return;
            }

            if (_selectedTrack is not null)
            {
                _selectedTrack.PropertyChanged -= SelectedTrack_PropertyChanged;
            }

            _selectedTrack = value;

            if (_selectedTrack is not null)
            {
                _selectedTrack.PropertyChanged += SelectedTrack_PropertyChanged;
            }

            _suppressPlaybackStopForSelectionChange = true;
            StopPlayback();
            _suppressPlaybackStopForSelectionChange = false;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasSelection));
            OnPropertyChanged(nameof(ActiveTrackLabel));
            RaiseTrackReadoutProperties();
            _ = LoadSelectedTrackAudioAsync(value);
        }
    }

    public int TrackCount => Tracks.Count;

    public bool HasSelection => SelectedTrack is not null;

    public bool IsRenamerExpanded
    {
        get => _isRenamerExpanded;
        set
        {
            if (_isRenamerExpanded != value)
            {
                _isRenamerExpanded = value;
                OnPropertyChanged();
            }
        }
    }

    public string LoadStatus
    {
        get => _loadStatus;
        set
        {
            if (_loadStatus != value)
            {
                _loadStatus = value;
                OnPropertyChanged();
            }
        }
    }

    public int ExportProgressPercent
    {
        get => _exportProgressPercent;
        set
        {
            if (_exportProgressPercent != value)
            {
                _exportProgressPercent = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ExportProgressText));
            }
        }
    }

    public string ExportProgressText => $"{ExportProgressPercent}%";

    public double DefaultClipSeconds
    {
        get => _defaultClipSeconds;
        set
        {
            if (Math.Abs(_defaultClipSeconds - value) > 0.001d)
            {
                _defaultClipSeconds = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(DefaultClipText));
            }
        }
    }

    public double DefaultFadeInSeconds
    {
        get => _defaultFadeInSeconds;
        set
        {
            if (Math.Abs(_defaultFadeInSeconds - value) > 0.001d)
            {
                _defaultFadeInSeconds = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(DefaultFadeInText));
            }
        }
    }

    public double DefaultFadeOutSeconds
    {
        get => _defaultFadeOutSeconds;
        set
        {
            if (Math.Abs(_defaultFadeOutSeconds - value) > 0.001d)
            {
                _defaultFadeOutSeconds = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(DefaultFadeOutText));
            }
        }
    }

    public string DefaultClipText => TrackItem.FormatTime(DefaultClipSeconds);

    public string DefaultFadeInText => TrackItem.FormatTime(DefaultFadeInSeconds);

    public string DefaultFadeOutText => TrackItem.FormatTime(DefaultFadeOutSeconds);

    public string ActiveTrackLabel => SelectedTrack?.FileName ?? "No track selected";

    public string DurationText => SelectedTrack?.DurationText ?? "0:00";

    public string StartText => SelectedTrack?.SelectionStartText ?? "0:00";

    public string EndText => SelectedTrack?.SelectionEndText ?? "0:00";

    public string ClipLengthText => SelectedTrack?.ClipLengthText ?? "0:00";

    public string FadeInText => SelectedTrack?.FadeInText ?? "0:00";

    public string FadeOutText => SelectedTrack?.FadeOutText ?? "0:00";

    public string GainText => SelectedTrack?.GainText ?? "0.0 dB";

    public float[] CurrentPeaks => SelectedTrack?.Peaks ?? Array.Empty<float>();

    public double CurrentSelectionStartRatio => SelectedTrack?.SelectionStartRatio ?? 0d;

    public double CurrentSelectionEndRatio => SelectedTrack?.SelectionEndRatio ?? 1d;

    public string CurrentPositionText
    {
        get => _currentPositionText;
        set
        {
            if (_currentPositionText != value)
            {
                _currentPositionText = value;
                OnPropertyChanged();
            }
        }
    }

    public double? PlayheadRatio
    {
        get => _playheadRatio;
        set
        {
            if (_playheadRatio != value)
            {
                _playheadRatio = value;
                OnPropertyChanged();
            }
        }
    }

    public RenamePatternOption SelectedRenamePattern
    {
        get => _selectedRenamePattern;
        set
        {
            if (_selectedRenamePattern != value)
            {
                _selectedRenamePattern = value;
                OnPropertyChanged();
            }
        }
    }

    public string RenameStatusText
    {
        get => _renameStatusText;
        set
        {
            if (_renameStatusText != value)
            {
                _renameStatusText = value;
                OnPropertyChanged();
            }
        }
    }

    private async void AddFilesButton_Click(object sender, RoutedEventArgs e)
    {
        await PromptAndAddFilesAsync();
    }

    private async void MenuImportFiles_Click(object sender, RoutedEventArgs e)
    {
        await PromptAndAddFilesAsync();
    }

    private void MenuExit_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void MenuAbout_Click(object sender, RoutedEventArgs e)
    {
        const string message =
            "MP3 Clip Editor\n" +
            "Version 1.0.0\n\n" +
            "Desktop MP3 clip editing, preview, tagging, renaming, and export powered by NAudio, TagLibSharp, and FFmpeg.";

        MessageBox.Show(this, message, "About MP3 Clip Editor", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void MenuInstructions_Click(object sender, RoutedEventArgs e)
    {
        const string message =
            "Instructions\n\n" +
            "1. Use File > Import MP3 Files or drag MP3 files into the window.\n" +
            "2. Select a track in the batch queue to load its waveform.\n" +
            "3. Drag the trim handles at the bottom of the waveform to set clip start and end.\n" +
            "4. Click inside the selected waveform area to preview from that point.\n" +
            "5. Adjust fades, gain, tags, and normalization as needed.\n" +
            "6. Use the Bulk Renamer panel to parse filenames, rename files, or write artist/title tags back to source MP3s.\n" +
            "7. Export the active track or use Export All Tracks for batch export.\n\n" +
            "For best results, rename files in the format: Artist - Title.mp3";

        MessageBox.Show(this, message, "Instructions", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async Task PromptAndAddFilesAsync()
    {
        var dialog = new OpenFileDialog
        {
            Filter = "MP3 files (*.mp3)|*.mp3",
            Multiselect = true,
            Title = "Choose MP3 files"
        };

        if (dialog.ShowDialog(this) == true)
        {
            await AddFilesAsync(dialog.FileNames);
        }
    }

    private async void SaveActiveButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedTrack is null)
        {
            return;
        }

        var dialog = new SaveFileDialog
        {
            Filter = "MP3 files (*.mp3)|*.mp3",
            FileName = SelectedTrack.OutputFileName,
            Title = "Save active clip"
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            LoadStatus = $"Saving {SelectedTrack.FileName}";
            ExportProgressPercent = 10;
            await _ffmpegService.ExportTrackAsync(SelectedTrack, dialog.FileName);
            ExportProgressPercent = 100;
            LoadStatus = "Active clip saved";
        }
        catch (Exception error)
        {
            ExportProgressPercent = 0;
            LoadStatus = "Save failed";
            MessageBox.Show(this, error.Message, "Could Not Save Clip", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void ExportAllButton_Click(object sender, RoutedEventArgs e)
    {
        if (Tracks.Count == 0)
        {
            return;
        }

        var dialog = new OpenFolderDialog
        {
            Title = "Choose a folder for exported MP3 clips",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
            Multiselect = false
        };

        if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.FolderName))
        {
            return;
        }

        var exportFolderPath = dialog.FolderName;

        try
        {
            var previousSelection = SelectedTrack;
            var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            ExportProgressPercent = 0;
            LoadStatus = $"Exporting {Tracks.Count} track(s)";

            for (var index = 0; index < Tracks.Count; index++)
            {
                var track = Tracks[index];
                SelectedTrack = track;
                LoadStatus = $"Exporting {track.FileName}";
                var fileName = GetUniqueFileName(track.OutputFileName, usedNames);
                var outputPath = Path.Combine(exportFolderPath, fileName);
                await _ffmpegService.ExportTrackAsync(track, outputPath);
                ExportProgressPercent = (int)Math.Round(((index + 1d) / Tracks.Count) * 100d);
            }

            SelectedTrack = previousSelection ?? Tracks.FirstOrDefault();
            ExportProgressPercent = 100;
            LoadStatus = "Batch export complete";
        }
        catch (Exception error)
        {
            ExportProgressPercent = 0;
            LoadStatus = "Batch export failed";
            MessageBox.Show(this, error.Message, "Could Not Export Tracks", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void RemoveSelectedButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedTrack is null)
        {
            return;
        }

        StopPlayback();
        var index = Tracks.IndexOf(SelectedTrack);
        Tracks.Remove(SelectedTrack);
        SelectedTrack = Tracks.Count == 0 ? null : Tracks[Math.Clamp(index, 0, Tracks.Count - 1)];
        OnPropertyChanged(nameof(TrackCount));
        LoadStatus = Tracks.Count == 0 ? "Idle" : "Track removed";
    }

    private void ResetAllButton_Click(object sender, RoutedEventArgs e)
    {
        StopPlayback();
        Tracks.Clear();
        SelectedTrack = null;
        ExportProgressPercent = 0;
        LoadStatus = "Idle";
        OnPropertyChanged(nameof(TrackCount));
    }

    private void ApplyDefaultsButton_Click(object sender, RoutedEventArgs e)
    {
        foreach (var track in Tracks)
        {
            track.ApplyDefaults(DefaultClipSeconds, DefaultFadeInSeconds, DefaultFadeOutSeconds);
        }

        RaiseTrackReadoutProperties();
        LoadStatus = Tracks.Count == 0 ? "Idle" : "Defaults applied";
    }

    private async void PlayFullButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedTrack is null)
        {
            return;
        }

        await StartPlaybackAsync(false);
    }

    private async void PlaySelectionButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedTrack is null)
        {
            return;
        }

        await StartPlaybackAsync(true);
    }

    private void StopPlaybackButton_Click(object sender, RoutedEventArgs e)
    {
        StopPlayback();
    }

    private async void Window_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            return;
        }

        var paths = ((string[])e.Data.GetData(DataFormats.FileDrop)).Where(path => string.Equals(Path.GetExtension(path), ".mp3", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (paths.Length > 0)
        {
            await AddFilesAsync(paths);
        }
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        var hasFileDrop = e.Data.GetDataPresent(DataFormats.FileDrop);
        e.Effects = hasFileDrop ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        StopPlayback();
        _audioPreviewService.Dispose();
        base.OnClosing(e);
    }

    private async Task AddFilesAsync(IEnumerable<string> filePaths)
    {
        var files = filePaths
            .Where(path => File.Exists(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(path => Tracks.All(track => !string.Equals(track.FilePath, path, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        if (files.Count == 0)
        {
            LoadStatus = "No new MP3 files selected";
            return;
        }

        try
        {
            for (var index = 0; index < files.Count; index++)
            {
                var filePath = files[index];
                LoadStatus = $"Queueing {index + 1}/{files.Count}: {Path.GetFileName(filePath)}";
                var track = await _ffmpegService.LoadTrackAsync(filePath, DefaultClipSeconds, DefaultFadeInSeconds, DefaultFadeOutSeconds);
                Tracks.Add(track);
                SelectedTrack ??= track;
                OnPropertyChanged(nameof(TrackCount));
            }

            LoadStatus = "Tracks added to queue";
        }
        catch (Exception error)
        {
            LoadStatus = "Import failed";
            MessageBox.Show(this, error.Message, "Could Not Load MP3", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task StartPlaybackAsync(bool selectionOnly)
    {
        if (SelectedTrack is null)
        {
            return;
        }

        try
        {
            StopPlayback();
            if (selectionOnly && SelectedTrack.NormalizeEnabled && !SelectedTrack.IsAudioLoaded)
            {
                await LoadSelectedTrackAudioAsync(SelectedTrack);
            }

            var startSeconds = selectionOnly ? SelectedTrack.SelectionStartSeconds : 0d;
            _isSelectionPreviewPlayback = selectionOnly;
            _applyPreviewFadeIn = selectionOnly;
            _applyPreviewFadeOut = selectionOnly;
            _previewFadeInStartSeconds = startSeconds;
            _audioPreviewService.Start(SelectedTrack.FilePath, startSeconds, 0f);
            ApplyPreviewGain();
            _playbackTimer.Start();
            LoadStatus = selectionOnly ? "Previewing selection" : "Previewing full track";
        }
        catch (Exception error)
        {
            StopPlayback();
            LoadStatus = "Preview failed";
            MessageBox.Show(this, error.Message, "Could Not Start Preview", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task StartPlaybackFromSelectionPositionAsync(double startSeconds)
    {
        if (SelectedTrack is null)
        {
            return;
        }

        try
        {
            StopPlayback();
            if (SelectedTrack.NormalizeEnabled && !SelectedTrack.IsAudioLoaded)
            {
                await LoadSelectedTrackAudioAsync(SelectedTrack);
            }

            _isSelectionPreviewPlayback = true;
            _applyPreviewFadeIn = false;
            _applyPreviewFadeOut = false;
            _previewFadeInStartSeconds = startSeconds;
            _audioPreviewService.Start(SelectedTrack.FilePath, startSeconds, 0f);
            ApplyPreviewGain();
            _playbackTimer.Start();
            LoadStatus = $"Previewing from {TrackItem.FormatTime(startSeconds)}";
        }
        catch (Exception error)
        {
            StopPlayback();
            LoadStatus = "Preview failed";
            MessageBox.Show(this, error.Message, "Could Not Start Preview", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void StopPlayback()
    {
        _isSelectionPreviewPlayback = false;
        _applyPreviewFadeIn = false;
        _applyPreviewFadeOut = false;
        _playbackTimer.Stop();
        CurrentPositionText = SelectedTrack?.SelectionStartText ?? "0:00";
        PlayheadRatio = null;
        _audioPreviewService.Stop();
    }

    private void PlaybackTimer_Tick(object? sender, EventArgs e)
    {
        if (SelectedTrack is null || !_audioPreviewService.IsPlaying)
        {
            CurrentPositionText = "0:00";
            PlayheadRatio = null;
            return;
        }

        var currentSeconds = _audioPreviewService.CurrentTimeSeconds;

        if (_isSelectionPreviewPlayback)
        {
            var selectionEnd = SelectedTrack.SelectionEndSeconds;
            if (currentSeconds >= selectionEnd)
            {
                StopPlayback();
                LoadStatus = "Preview stopped";
                return;
            }
        }

        ApplyPreviewGain();
        CurrentPositionText = TrackItem.FormatTime(currentSeconds);
        PlayheadRatio = SelectedTrack.DurationSeconds <= 0 ? null : currentSeconds / SelectedTrack.DurationSeconds;
    }

    private void AudioPreviewService_PlaybackStopped(object? sender, EventArgs e)
    {
        _ = Dispatcher.BeginInvoke(() =>
        {
            StopPlayback();
            if (SelectedTrack is not null)
            {
                CurrentPositionText = SelectedTrack.SelectionStartText;
            }

            if (LoadStatus.StartsWith("Preview", StringComparison.OrdinalIgnoreCase))
            {
                LoadStatus = "Preview stopped";
            }
        });
    }

    private void SelectedTrack_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        var track = SelectedTrack;

        if (!_suppressPlaybackStopForSelectionChange && IsSelectionBoundaryProperty(e.PropertyName) && _audioPreviewService.IsPlaying && _isSelectionPreviewPlayback && track is not null)
        {
            var currentSeconds = _audioPreviewService.CurrentTimeSeconds;
            if (currentSeconds >= track.SelectionEndSeconds)
            {
                StopPlayback();
                LoadStatus = "Preview stopped";
            }
        }
        else if (!_suppressPlaybackStopForSelectionChange && IsPlaybackSensitiveProperty(e.PropertyName))
        {
            if (e.PropertyName == nameof(TrackItem.NormalizeEnabled) && track is not null && track.NormalizeEnabled && !track.IsAudioLoaded)
            {
                StopPlayback();
                LoadStatus = "Preview stopped after edit";
            }
            else
            {
                ApplyPreviewGain();
            }
        }

        RaiseTrackReadoutProperties();
    }

    private async Task LoadSelectedTrackAudioAsync(TrackItem? track)
    {
        if (track is null || track.IsAudioLoaded)
        {
            return;
        }

        var version = ++_selectionLoadVersion;

        try
        {
            LoadStatus = $"Loading waveform for {track.FileName}";
            await _ffmpegService.EnsureTrackAudioLoadedAsync(track);

            if (version != _selectionLoadVersion)
            {
                return;
            }

            if (ReferenceEquals(track, SelectedTrack))
            {
                OnPropertyChanged(nameof(SelectedTrack));
                RaiseTrackReadoutProperties();
            }

            LoadStatus = $"Waveform ready for {track.FileName}";
        }
        catch (Exception error)
        {
            if (version != _selectionLoadVersion)
            {
                return;
            }

            LoadStatus = "Waveform load failed";
            MessageBox.Show(this, error.Message, "Could Not Load Waveform", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void TrackGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SelectedTrack is not null)
        {
            _ = LoadSelectedTrackAudioAsync(SelectedTrack);
        }
    }

    private void PlaybackSensitiveControl_Changed(object sender, RoutedEventArgs e)
    {
        if (SelectedTrack is null)
        {
            return;
        }

        if (sender is CheckBox checkBox
            && string.Equals(checkBox.Content?.ToString(), "Normalize clip volume", StringComparison.Ordinal)
            && SelectedTrack.NormalizeEnabled
            && !SelectedTrack.IsAudioLoaded)
        {
            StopPlayback();
            LoadStatus = "Preview stopped after edit";
            return;
        }

        ApplyPreviewGain();
    }

    private async void WaveformView_SeekRequested(object sender, WaveformSeekRequestedEventArgs e)
    {
        if (SelectedTrack is null)
        {
            return;
        }

        await StartPlaybackFromSelectionPositionAsync(e.Seconds);
    }

    private void RaiseTrackReadoutProperties()
    {
        OnPropertyChanged(nameof(DurationText));
        OnPropertyChanged(nameof(StartText));
        OnPropertyChanged(nameof(EndText));
        OnPropertyChanged(nameof(ClipLengthText));
        OnPropertyChanged(nameof(FadeInText));
        OnPropertyChanged(nameof(FadeOutText));
        OnPropertyChanged(nameof(GainText));
        OnPropertyChanged(nameof(ActiveTrackLabel));
        OnPropertyChanged(nameof(CurrentPeaks));
        OnPropertyChanged(nameof(CurrentSelectionStartRatio));
        OnPropertyChanged(nameof(CurrentSelectionEndRatio));
    }

    private void ApplyFilenameTagsButton_Click(object sender, RoutedEventArgs e)
    {
        if (Tracks.Count == 0)
        {
            RenameStatusText = "No tracks in the queue.";
            return;
        }

        _filenameMetadataService.ApplyFilenameTags(Tracks, SelectedRenamePattern.Key);
        RaiseTrackReadoutProperties();
        RenameStatusText = $"Applied filename parsing to {Tracks.Count} queued track(s).";
        LoadStatus = "Filename tags applied";
    }

    private async void WriteQueueTagsButton_Click(object sender, RoutedEventArgs e)
    {
        if (Tracks.Count == 0)
        {
            RenameStatusText = "No tracks in the queue.";
            return;
        }

        try
        {
            await Task.Run(() => _filenameMetadataService.WriteTagsToSourceFiles(Tracks));
            RenameStatusText = $"Wrote artist/title tags to {Tracks.Count} source MP3 file(s).";
            LoadStatus = "Source tags updated";
        }
        catch (Exception error)
        {
            RenameStatusText = "Could not write source tags.";
            MessageBox.Show(this, error.Message, "Could Not Write Tags", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void RenameQueueFilesButton_Click(object sender, RoutedEventArgs e)
    {
        if (Tracks.Count == 0)
        {
            RenameStatusText = "No tracks in the queue.";
            return;
        }

        try
        {
            StopPlayback();
            var renamed = await Task.Run(() => _filenameMetadataService.RenameTracksToArtistTitle(Tracks));
            RaiseTrackReadoutProperties();
            RenameStatusText = renamed == 0
                ? "Queue filenames were already in Artist - Title format."
                : $"Renamed {renamed} file(s) to Artist - Title.mp3.";
            LoadStatus = "Queue files renamed";
        }
        catch (Exception error)
        {
            RenameStatusText = "Could not rename queue files.";
            MessageBox.Show(this, error.Message, "Could Not Rename Files", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ToggleRenamerButton_Click(object sender, RoutedEventArgs e)
    {
        IsRenamerExpanded = !IsRenamerExpanded;
    }

    private void ApplyPreviewGain()
    {
        if (SelectedTrack is null || !_audioPreviewService.IsPlaying)
        {
            return;
        }

        var currentSeconds = _audioPreviewService.CurrentTimeSeconds;
        var gain = _ffmpegService.GetPreviewGainMultiplier(SelectedTrack, _isSelectionPreviewPlayback);

        if (_applyPreviewFadeIn && SelectedTrack.FadeInSeconds > 0.001d)
        {
            var fadeInProgress = Math.Clamp((currentSeconds - _previewFadeInStartSeconds) / SelectedTrack.FadeInSeconds, 0d, 1d);
            gain *= fadeInProgress;
        }

        if (_applyPreviewFadeOut && SelectedTrack.FadeOutSeconds > 0.001d)
        {
            var remainingSeconds = Math.Max(0d, SelectedTrack.SelectionEndSeconds - currentSeconds);
            var fadeOutProgress = Math.Clamp(remainingSeconds / SelectedTrack.FadeOutSeconds, 0d, 1d);
            gain *= fadeOutProgress;
        }

        _audioPreviewService.SetGain((float)gain);
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private static bool IsPlaybackSensitiveProperty(string? propertyName)
    {
        return propertyName is nameof(TrackItem.FadeInSeconds)
            or nameof(TrackItem.FadeOutSeconds)
            or nameof(TrackItem.GainDb)
            or nameof(TrackItem.NormalizeEnabled);
    }

    private static bool IsSelectionBoundaryProperty(string? propertyName)
    {
        return propertyName is nameof(TrackItem.SelectionStartSeconds)
            or nameof(TrackItem.SelectionEndSeconds);
    }

    private static string GetUniqueFileName(string desiredName, ISet<string> usedNames)
    {
        var baseName = Path.GetFileNameWithoutExtension(desiredName);
        var extension = Path.GetExtension(desiredName);
        var candidate = desiredName;
        var counter = 2;

        while (!usedNames.Add(candidate))
        {
            candidate = $"{baseName} ({counter}){extension}";
            counter++;
        }

        return candidate;
    }
}

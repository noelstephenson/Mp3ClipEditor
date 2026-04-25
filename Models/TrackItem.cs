using System.Globalization;
using System.IO;
using Mp3ClipEditorTagger.Infrastructure;

namespace Mp3ClipEditorTagger.Models;

public sealed class TrackItem : ObservableObject
{
    private const double MinClipGapSeconds = 0.1;

    private string _filePath = string.Empty;
    private string _fileName = string.Empty;
    private string _fileBaseName = string.Empty;
    private string _sourceArtist = string.Empty;
    private string _sourceTitle = string.Empty;
    private string _filenameArtist = string.Empty;
    private string _filenameTitle = string.Empty;
    private double _durationSeconds;
    private double _selectionStartSeconds;
    private double _selectionEndSeconds;
    private double _fadeInSeconds;
    private double _fadeOutSeconds;
    private double _gainDb;
    private bool _normalizeEnabled = true;
    private bool _writeId3Enabled = true;
    private string _artist = string.Empty;
    private string _title = string.Empty;
    private bool _isAudioLoaded;
    private int _sampleRate = 44100;
    private int _channels = 2;
    private float[] _samples = Array.Empty<float>();
    private float[] _peaks = Array.Empty<float>();

    public required string Id { get; init; }
    public required string FilePath
    {
        get => _filePath;
        set => SetProperty(ref _filePath, value);
    }

    public required string FileName
    {
        get => _fileName;
        set => SetProperty(ref _fileName, value);
    }

    public required string FileBaseName
    {
        get => _fileBaseName;
        set => SetProperty(ref _fileBaseName, value);
    }

    public required double DurationSeconds
    {
        get => _durationSeconds;
        set
        {
            if (SetProperty(ref _durationSeconds, Math.Max(0d, value)))
            {
                ClampSelectionWithinDuration();
                OnPropertyChanged(nameof(DurationText));
                OnPropertyChanged(nameof(SelectionStartRatio));
                OnPropertyChanged(nameof(SelectionEndRatio));
            }
        }
    }

    public required string SourceArtist
    {
        get => _sourceArtist;
        set => SetProperty(ref _sourceArtist, value);
    }

    public required string SourceTitle
    {
        get => _sourceTitle;
        set => SetProperty(ref _sourceTitle, value);
    }

    public required string FilenameArtist
    {
        get => _filenameArtist;
        set => SetProperty(ref _filenameArtist, value);
    }

    public required string FilenameTitle
    {
        get => _filenameTitle;
        set => SetProperty(ref _filenameTitle, value);
    }

    public int SampleRate
    {
        get => _sampleRate;
        set => SetProperty(ref _sampleRate, value);
    }

    public int Channels
    {
        get => _channels;
        set => SetProperty(ref _channels, value);
    }

    public float[] Samples
    {
        get => _samples;
        private set => SetProperty(ref _samples, value);
    }

    public float[] Peaks
    {
        get => _peaks;
        private set => SetProperty(ref _peaks, value);
    }

    public bool IsAudioLoaded
    {
        get => _isAudioLoaded;
        private set => SetProperty(ref _isAudioLoaded, value);
    }

    public string Artist
    {
        get => _artist;
        set
        {
            if (SetProperty(ref _artist, value))
            {
                OnPropertyChanged(nameof(OutputFileName));
            }
        }
    }

    public string Title
    {
        get => _title;
        set
        {
            if (SetProperty(ref _title, value))
            {
                OnPropertyChanged(nameof(OutputFileName));
            }
        }
    }

    public double SelectionStartSeconds
    {
        get => _selectionStartSeconds;
        set
        {
            var clamped = Math.Clamp(value, 0, Math.Max(0, SelectionEndSeconds - MinClipGapSeconds));
            if (SetProperty(ref _selectionStartSeconds, clamped))
            {
                ClampFadeLengths();
                NotifySelectionChanged();
            }
        }
    }

    public double SelectionEndSeconds
    {
        get => _selectionEndSeconds;
        set
        {
            var clamped = Math.Clamp(value, SelectionStartSeconds + MinClipGapSeconds, DurationSeconds);
            if (SetProperty(ref _selectionEndSeconds, clamped))
            {
                ClampFadeLengths();
                NotifySelectionChanged();
            }
        }
    }

    public double FadeInSeconds
    {
        get => _fadeInSeconds;
        set
        {
            var clamped = Math.Clamp(value, 0, ClipLengthSeconds);
            if (SetProperty(ref _fadeInSeconds, clamped))
            {
                OnPropertyChanged(nameof(FadeInText));
            }
        }
    }

    public double FadeOutSeconds
    {
        get => _fadeOutSeconds;
        set
        {
            var clamped = Math.Clamp(value, 0, ClipLengthSeconds);
            if (SetProperty(ref _fadeOutSeconds, clamped))
            {
                OnPropertyChanged(nameof(FadeOutText));
            }
        }
    }

    public double GainDb
    {
        get => _gainDb;
        set
        {
            if (SetProperty(ref _gainDb, Math.Clamp(value, -24, 12)))
            {
                OnPropertyChanged(nameof(GainText));
            }
        }
    }

    public bool NormalizeEnabled
    {
        get => _normalizeEnabled;
        set => SetProperty(ref _normalizeEnabled, value);
    }

    public bool WriteId3Enabled
    {
        get => _writeId3Enabled;
        set => SetProperty(ref _writeId3Enabled, value);
    }

    public double ClipLengthSeconds => Math.Max(0, SelectionEndSeconds - SelectionStartSeconds);

    public string DurationText => FormatTime(DurationSeconds);

    public string SelectionStartText => FormatTime(SelectionStartSeconds);

    public string SelectionEndText => FormatTime(SelectionEndSeconds);

    public string ClipLengthText => FormatTime(ClipLengthSeconds);

    public string FadeInText => FormatTime(FadeInSeconds);

    public string FadeOutText => FormatTime(FadeOutSeconds);

    public string GainText
    {
        get
        {
            var rounded = Math.Round(GainDb, 1);
            var sign = rounded > 0 ? "+" : string.Empty;
            return $"{sign}{rounded.ToString("0.0", CultureInfo.InvariantCulture)} dB";
        }
    }

    public double SelectionStartRatio => DurationSeconds <= 0 ? 0 : SelectionStartSeconds / DurationSeconds;

    public double SelectionEndRatio => DurationSeconds <= 0 ? 0 : SelectionEndSeconds / DurationSeconds;

    public string OutputFileName => BuildOutputFileName(Artist, Title);

    public void ApplyDefaults(double clipSeconds, double fadeInSeconds, double fadeOutSeconds)
    {
        var clipLength = Math.Min(DurationSeconds, clipSeconds);
        SelectionStartSeconds = 0;
        SelectionEndSeconds = clipLength;
        FadeInSeconds = Math.Min(fadeInSeconds, clipLength);
        FadeOutSeconds = Math.Min(fadeOutSeconds, clipLength);
    }

    public void SetAudioData(int sampleRate, int channels, float[] samples, float[] peaks)
    {
        SampleRate = Math.Max(1, sampleRate);
        Channels = Math.Max(1, channels);
        Samples = samples;
        Peaks = peaks;
        var frameCount = Channels <= 0 ? 0d : samples.Length / (double)Channels;
        var decodedDurationSeconds = SampleRate <= 0 ? 0d : frameCount / SampleRate;
        if (decodedDurationSeconds > 0.01d)
        {
            DurationSeconds = decodedDurationSeconds;
        }

        IsAudioLoaded = true;
    }

    public void UpdateFileReference(string filePath)
    {
        FilePath = filePath;
        FileName = Path.GetFileName(filePath);
        FileBaseName = Path.GetFileNameWithoutExtension(filePath);
    }

    public void UpdateFilenameTags(string artist, string title)
    {
        FilenameArtist = artist;
        FilenameTitle = title;
    }

    public static string FormatTime(double seconds)
    {
        var safeSeconds = Math.Max(0, seconds);
        var minutes = (int)Math.Floor(safeSeconds / 60d);
        var remainder = (int)Math.Floor(safeSeconds % 60d);
        return $"{minutes}:{remainder:00}";
    }

    public static string BuildOutputFileName(string? artist, string? title)
    {
        var safeArtist = SanitizeFileNamePart(string.IsNullOrWhiteSpace(artist) ? "Unknown Artist" : artist!.Trim());
        var safeTitle = SanitizeFileNamePart(string.IsNullOrWhiteSpace(title) ? "Untitled Clip" : title!.Trim());
        return CollapseRepeatedClipWords($"{safeArtist} - {safeTitle}.mp3");
    }

    private static string SanitizeFileNamePart(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(value.Where(ch => !invalid.Contains(ch)).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? "clip" : cleaned;
    }

    private static string CollapseRepeatedClipWords(string fileName)
    {
        return fileName
            .Replace("clip clip", "clip", StringComparison.OrdinalIgnoreCase)
            .Replace("Clip Clip", "Clip", StringComparison.OrdinalIgnoreCase);
    }

    private void ClampFadeLengths()
    {
        var clipLength = ClipLengthSeconds;
        if (FadeInSeconds > clipLength)
        {
            _fadeInSeconds = clipLength;
            OnPropertyChanged(nameof(FadeInSeconds));
            OnPropertyChanged(nameof(FadeInText));
        }

        if (FadeOutSeconds > clipLength)
        {
            _fadeOutSeconds = clipLength;
            OnPropertyChanged(nameof(FadeOutSeconds));
            OnPropertyChanged(nameof(FadeOutText));
        }
    }

    private void NotifySelectionChanged()
    {
        OnPropertyChanged(nameof(SelectionStartText));
        OnPropertyChanged(nameof(SelectionEndText));
        OnPropertyChanged(nameof(ClipLengthSeconds));
        OnPropertyChanged(nameof(ClipLengthText));
        OnPropertyChanged(nameof(SelectionStartRatio));
        OnPropertyChanged(nameof(SelectionEndRatio));
    }

    private void ClampSelectionWithinDuration()
    {
        var safeDuration = Math.Max(0d, DurationSeconds);

        if (_selectionEndSeconds > safeDuration)
        {
            _selectionEndSeconds = safeDuration;
            OnPropertyChanged(nameof(SelectionEndSeconds));
            OnPropertyChanged(nameof(SelectionEndText));
        }

        var maxStart = Math.Max(0d, _selectionEndSeconds - MinClipGapSeconds);
        if (_selectionStartSeconds > maxStart)
        {
            _selectionStartSeconds = maxStart;
            OnPropertyChanged(nameof(SelectionStartSeconds));
            OnPropertyChanged(nameof(SelectionStartText));
        }

        ClampFadeLengths();
        OnPropertyChanged(nameof(ClipLengthSeconds));
        OnPropertyChanged(nameof(ClipLengthText));
    }
}

using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mp3ClipEditorTagger.Models;

namespace Mp3ClipEditorTagger.Services;

public sealed class FfmpegService
{
    private readonly string _ffmpegPath;
    private readonly string _ffprobePath;
    private readonly string _ffplayPath;

    public FfmpegService()
    {
        var baseDirectory = AppContext.BaseDirectory;
        _ffmpegPath = Path.Combine(baseDirectory, "tools", "ffmpeg", "bin", "ffmpeg.exe");
        _ffprobePath = Path.Combine(baseDirectory, "tools", "ffmpeg", "bin", "ffprobe.exe");
        _ffplayPath = Path.Combine(baseDirectory, "tools", "ffmpeg", "bin", "ffplay.exe");
    }

    public void EnsureAvailable()
    {
        if (!File.Exists(_ffmpegPath) || !File.Exists(_ffprobePath) || !File.Exists(_ffplayPath))
        {
            throw new FileNotFoundException("Bundled FFmpeg tools were not found in the application output.");
        }
    }

    public async Task<TrackItem> LoadTrackAsync(
        string filePath,
        double defaultClipSeconds,
        double defaultFadeInSeconds,
        double defaultFadeOutSeconds,
        CancellationToken cancellationToken = default)
    {
        EnsureAvailable();

        var probe = await ProbeAsync(filePath, cancellationToken);
        var durationSeconds = probe.Format?.DurationSeconds
            ?? probe.Streams?.FirstOrDefault(stream => stream.CodecType == "audio")?.DurationSeconds
            ?? 0d;

        var fileName = Path.GetFileName(filePath);
        var fileBaseName = Path.GetFileNameWithoutExtension(filePath);
        var filenameTags = ParseTagsFromFileName(fileBaseName);
        var sourceArtist = probe.Format?.Tags?.Artist ?? string.Empty;
        var sourceTitle = probe.Format?.Tags?.Title ?? string.Empty;
        var defaultTags = ComputeDefaultTags(sourceArtist, sourceTitle, filenameTags.artist, filenameTags.title);

        var track = new TrackItem
        {
            Id = Guid.NewGuid().ToString("N"),
            FilePath = filePath,
            FileName = fileName,
            FileBaseName = fileBaseName,
            DurationSeconds = durationSeconds,
            SourceArtist = sourceArtist,
            SourceTitle = sourceTitle,
            FilenameArtist = filenameTags.artist,
            FilenameTitle = filenameTags.title,
            Artist = defaultTags.artist,
            Title = defaultTags.title
        };

        track.ApplyDefaults(defaultClipSeconds, defaultFadeInSeconds, defaultFadeOutSeconds);
        track.NormalizeEnabled = true;
        track.WriteId3Enabled = true;
        return track;
    }

    public async Task EnsureTrackAudioLoadedAsync(TrackItem track, CancellationToken cancellationToken = default)
    {
        if (track.IsAudioLoaded)
        {
            return;
        }

        EnsureAvailable();
        var probe = await ProbeAsync(track.FilePath, cancellationToken);
        var samples = await DecodeSamplesAsync(track.FilePath, cancellationToken);
        track.SetAudioData(probe.AudioSampleRate, probe.AudioChannels, samples, BuildPeaks(samples, probe.AudioChannels, 1400));
    }

    public Process StartPreview(TrackItem track, bool playSelection)
    {
        EnsureAvailable();

        if (playSelection)
        {
            return StartPreview(track, track.SelectionStartSeconds, track.DurationSeconds, true, true, false);
        }

        return StartPreview(track, 0d, track.DurationSeconds, false, false, false);
    }

    public Process StartPreviewFromSelectionPosition(TrackItem track, double startSeconds, bool applyFadeIn = false, bool applyFadeOut = false)
    {
        EnsureAvailable();
        var clampedStart = Math.Clamp(startSeconds, track.SelectionStartSeconds, track.SelectionEndSeconds);
        return StartPreview(track, clampedStart, track.DurationSeconds, true, applyFadeIn, applyFadeOut);
    }

    private Process StartPreview(TrackItem track, double startSeconds, double endSeconds, bool applySelectionProcessing, bool applyFadeIn, bool applyFadeOut)
    {
        var durationSeconds = Math.Max(0.05d, endSeconds - startSeconds);
        var filters = BuildFilterChain(track, applySelectionProcessing, startSeconds, applyFadeIn, applyFadeOut);
        var startInfo = new ProcessStartInfo(_ffplayPath)
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };

        startInfo.ArgumentList.Add("-nodisp");
        startInfo.ArgumentList.Add("-autoexit");
        startInfo.ArgumentList.Add("-hide_banner");
        startInfo.ArgumentList.Add("-loglevel");
        startInfo.ArgumentList.Add("error");
        startInfo.ArgumentList.Add("-ss");
        startInfo.ArgumentList.Add(FormatInvariant(startSeconds));
        startInfo.ArgumentList.Add("-t");
        startInfo.ArgumentList.Add(FormatInvariant(durationSeconds));
        startInfo.ArgumentList.Add("-i");
        startInfo.ArgumentList.Add(track.FilePath);

        if (!string.IsNullOrWhiteSpace(filters))
        {
            startInfo.ArgumentList.Add("-af");
            startInfo.ArgumentList.Add(filters);
        }

        var process = new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true
        };

        process.Start();
        _ = process.StandardOutput.ReadToEndAsync();
        _ = process.StandardError.ReadToEndAsync();
        return process;
    }

    public async Task ExportTrackAsync(TrackItem track, string outputPath, CancellationToken cancellationToken = default)
    {
        EnsureAvailable();

        if (track.NormalizeEnabled)
        {
            await EnsureTrackAudioLoadedAsync(track, cancellationToken);
        }

        var startInfo = new ProcessStartInfo(_ffmpegPath)
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };

        startInfo.ArgumentList.Add("-y");
        startInfo.ArgumentList.Add("-hide_banner");
        startInfo.ArgumentList.Add("-loglevel");
        startInfo.ArgumentList.Add("error");
        startInfo.ArgumentList.Add("-ss");
        startInfo.ArgumentList.Add(FormatInvariant(track.SelectionStartSeconds));
        startInfo.ArgumentList.Add("-t");
        startInfo.ArgumentList.Add(FormatInvariant(track.ClipLengthSeconds));
        startInfo.ArgumentList.Add("-i");
        startInfo.ArgumentList.Add(track.FilePath);

        var filters = BuildFilterChain(track, true, track.SelectionStartSeconds, true, true);
        if (!string.IsNullOrWhiteSpace(filters))
        {
            startInfo.ArgumentList.Add("-af");
            startInfo.ArgumentList.Add(filters);
        }

        startInfo.ArgumentList.Add("-vn");
        startInfo.ArgumentList.Add("-map_metadata");
        startInfo.ArgumentList.Add("-1");
        startInfo.ArgumentList.Add("-codec:a");
        startInfo.ArgumentList.Add("libmp3lame");
        startInfo.ArgumentList.Add("-b:a");
        startInfo.ArgumentList.Add("128k");
        startInfo.ArgumentList.Add("-id3v2_version");
        startInfo.ArgumentList.Add("3");

        if (track.WriteId3Enabled)
        {
            if (!string.IsNullOrWhiteSpace(track.Artist))
            {
                startInfo.ArgumentList.Add("-metadata");
                startInfo.ArgumentList.Add($"artist={track.Artist.Trim()}");
            }

            if (!string.IsNullOrWhiteSpace(track.Title))
            {
                startInfo.ArgumentList.Add("-metadata");
                startInfo.ArgumentList.Add($"title={track.Title.Trim()}");
            }
        }

        startInfo.ArgumentList.Add(outputPath);
        await RunProcessExpectSuccessAsync(startInfo, cancellationToken);
    }

    public double GetPreviewGainMultiplier(TrackItem track, bool selectionOnly)
    {
        var totalGain = Math.Pow(10d, track.GainDb / 20d);
        if (selectionOnly && track.NormalizeEnabled)
        {
            totalGain *= GetNormalizationGain(track);
        }

        return totalGain;
    }

    public async Task ExportTracksToFolderAsync(
        IReadOnlyList<TrackItem> tracks,
        string folderPath,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        EnsureAvailable();

        Directory.CreateDirectory(folderPath);
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var index = 0; index < tracks.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var track = tracks[index];
            var fileName = GetUniqueFileName(track.OutputFileName, usedNames);
            var outputPath = Path.Combine(folderPath, fileName);
            await ExportTrackAsync(track, outputPath, cancellationToken);
            progress?.Report((int)Math.Round(((index + 1d) / Math.Max(1, tracks.Count)) * 100d));
        }
    }

    private async Task<FfprobeResponse> ProbeAsync(string filePath, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(_ffprobePath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        startInfo.ArgumentList.Add("-v");
        startInfo.ArgumentList.Add("quiet");
        startInfo.ArgumentList.Add("-print_format");
        startInfo.ArgumentList.Add("json");
        startInfo.ArgumentList.Add("-show_format");
        startInfo.ArgumentList.Add("-show_streams");
        startInfo.ArgumentList.Add(filePath);

        using var process = new Process { StartInfo = startInfo };
        process.Start();
        var stdOutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stdErrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        var standardOutput = await stdOutTask;
        var standardError = await stdErrTask;

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"ffprobe failed for {Path.GetFileName(filePath)}: {standardError}");
        }

        var response = JsonSerializer.Deserialize<FfprobeResponse>(standardOutput, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        if (response is null)
        {
            throw new InvalidOperationException($"ffprobe returned unreadable metadata for {Path.GetFileName(filePath)}.");
        }

        var audioStream = response.Streams?.FirstOrDefault(stream => string.Equals(stream.CodecType, "audio", StringComparison.OrdinalIgnoreCase));
        response.AudioChannels = Math.Max(1, audioStream?.Channels ?? 2);
        response.AudioSampleRate = Math.Max(1, audioStream?.SampleRateValue ?? 44100);
        return response;
    }

    private async Task<float[]> DecodeSamplesAsync(string filePath, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(_ffmpegPath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        startInfo.ArgumentList.Add("-hide_banner");
        startInfo.ArgumentList.Add("-loglevel");
        startInfo.ArgumentList.Add("error");
        startInfo.ArgumentList.Add("-i");
        startInfo.ArgumentList.Add(filePath);
        startInfo.ArgumentList.Add("-map");
        startInfo.ArgumentList.Add("0:a:0");
        startInfo.ArgumentList.Add("-f");
        startInfo.ArgumentList.Add("f32le");
        startInfo.ArgumentList.Add("-acodec");
        startInfo.ArgumentList.Add("pcm_f32le");
        startInfo.ArgumentList.Add("pipe:1");

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        await using var memoryStream = new MemoryStream();
        var copyTask = process.StandardOutput.BaseStream.CopyToAsync(memoryStream, cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await Task.WhenAll(copyTask, process.WaitForExitAsync(cancellationToken));
        var standardError = await errorTask;

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"ffmpeg could not decode {Path.GetFileName(filePath)}: {standardError}");
        }

        var bytes = memoryStream.ToArray();
        var samples = new float[bytes.Length / sizeof(float)];
        Buffer.BlockCopy(bytes, 0, samples, 0, bytes.Length);
        return samples;
    }

    private async Task RunProcessExpectSuccessAsync(ProcessStartInfo startInfo, CancellationToken cancellationToken)
    {
        using var process = new Process { StartInfo = startInfo };
        process.Start();
        var stdOutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stdErrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var standardOutput = await stdOutTask;
        var standardError = await stdErrTask;

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(standardError) ? standardOutput : standardError);
        }
    }

    private static string BuildFilterChain(TrackItem track, bool selectionOnly, double previewStartSeconds, bool applyFadeIn, bool applyFadeOut)
    {
        var filters = new List<string>();
        var totalGain = Math.Pow(10d, track.GainDb / 20d);

        if (selectionOnly && track.NormalizeEnabled)
        {
            totalGain *= GetNormalizationGain(track);
        }

        if (Math.Abs(totalGain - 1d) > 0.0001d)
        {
            filters.Add($"volume={FormatInvariant(totalGain)}");
        }

        if (selectionOnly && applyFadeIn && track.FadeInSeconds > 0.001d)
        {
            filters.Add($"afade=t=in:st=0:d={FormatInvariant(track.FadeInSeconds)}");
        }

        if (selectionOnly && applyFadeOut && track.FadeOutSeconds > 0.001d)
        {
            var remainingSeconds = Math.Max(0d, track.SelectionEndSeconds - previewStartSeconds);
            var fadeDuration = Math.Min(track.FadeOutSeconds, remainingSeconds);
            if (fadeDuration > 0.001d)
            {
                var fadeStart = Math.Max(0d, remainingSeconds - fadeDuration);
                filters.Add($"afade=t=out:st={FormatInvariant(fadeStart)}:d={FormatInvariant(fadeDuration)}");
            }
        }

        return string.Join(",", filters);
    }

    private static double GetNormalizationGain(TrackItem track)
    {
        var channels = Math.Max(1, track.Channels);
        var startFrame = (int)Math.Floor(track.SelectionStartSeconds * track.SampleRate);
        var endFrame = (int)Math.Ceiling(track.SelectionEndSeconds * track.SampleRate);
        startFrame = Math.Max(0, startFrame);
        endFrame = Math.Min(track.Samples.Length / channels, endFrame);

        var peak = 0d;
        for (var frame = startFrame; frame < endFrame; frame++)
        {
            var baseIndex = frame * channels;
            for (var channel = 0; channel < channels; channel++)
            {
                var value = Math.Abs(track.Samples[baseIndex + channel]);
                if (value > peak)
                {
                    peak = value;
                }
            }
        }

        return peak <= 0 ? 1d : 1d / peak;
    }

    private static float[] BuildPeaks(float[] samples, int channels, int bucketCount)
    {
        var safeChannels = Math.Max(1, channels);
        var frameCount = samples.Length / safeChannels;
        if (frameCount == 0)
        {
            return Array.Empty<float>();
        }

        var peaks = new float[Math.Min(bucketCount, Math.Max(1, frameCount))];
        var framesPerBucket = Math.Max(1, frameCount / peaks.Length);
        var globalPeak = 0f;

        for (var bucketIndex = 0; bucketIndex < peaks.Length; bucketIndex++)
        {
            var startFrame = bucketIndex * framesPerBucket;
            var endFrame = bucketIndex == peaks.Length - 1 ? frameCount : Math.Min(frameCount, startFrame + framesPerBucket);
            var peak = 0f;

            for (var frame = startFrame; frame < endFrame; frame++)
            {
                var baseIndex = frame * safeChannels;
                for (var channel = 0; channel < safeChannels; channel++)
                {
                    var value = Math.Abs(samples[baseIndex + channel]);
                    if (value > peak)
                    {
                        peak = value;
                    }
                }
            }

            peaks[bucketIndex] = peak;
            if (peak > globalPeak)
            {
                globalPeak = peak;
            }
        }

        if (globalPeak <= 0)
        {
            return peaks;
        }

        for (var index = 0; index < peaks.Length; index++)
        {
            peaks[index] /= globalPeak;
        }

        return peaks;
    }

    private static (string artist, string title) ParseTagsFromFileName(string fileBaseName)
    {
        var parts = fileBaseName.Split(" - ", 2, StringSplitOptions.TrimEntries);
        return parts.Length == 2
            ? (parts[0], parts[1])
            : (string.Empty, fileBaseName);
    }

    private static (string artist, string title) ComputeDefaultTags(string sourceArtist, string sourceTitle, string filenameArtist, string filenameTitle)
    {
        var hasFilePattern = !string.IsNullOrWhiteSpace(filenameArtist) && !string.IsNullOrWhiteSpace(filenameTitle);
        if (hasFilePattern && (!NormalizedEquals(sourceArtist, filenameArtist) || !NormalizedEquals(sourceTitle, filenameTitle)))
        {
            return (filenameArtist, filenameTitle);
        }

        return (
            string.IsNullOrWhiteSpace(sourceArtist) ? filenameArtist : sourceArtist,
            string.IsNullOrWhiteSpace(sourceTitle) ? filenameTitle : sourceTitle);
    }

    private static bool NormalizedEquals(string? left, string? right)
    {
        static string Normalize(string? value) => (value ?? string.Empty).Trim().ToLowerInvariant();
        return Normalize(left) == Normalize(right);
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

    private static string FormatInvariant(double value)
        => value.ToString("0.###", CultureInfo.InvariantCulture);

    private sealed class FfprobeResponse
    {
        public List<FfprobeStream>? Streams { get; set; }
        public FfprobeFormat? Format { get; set; }
        public int AudioChannels { get; set; }
        public int AudioSampleRate { get; set; }
    }

    private sealed class FfprobeStream
    {
        [JsonPropertyName("codec_type")]
        public string? CodecType { get; set; }

        public int? Channels { get; set; }

        [JsonPropertyName("sample_rate")]
        public string? SampleRate { get; set; }

        public string? Duration { get; set; }

        public int? SampleRateParsed => int.TryParse(SampleRate, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;
        public double? DurationSeconds => double.TryParse(Duration, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null;
        public int? SampleRateValue => SampleRateParsed;
    }

    private sealed class FfprobeFormat
    {
        public string? Duration { get; set; }
        public FfprobeTags? Tags { get; set; }
        public double? DurationSeconds => double.TryParse(Duration, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null;
    }

    private sealed class FfprobeTags
    {
        [JsonPropertyName("artist")]
        public string? Artist { get; set; }

        [JsonPropertyName("title")]
        public string? Title { get; set; }
    }
}

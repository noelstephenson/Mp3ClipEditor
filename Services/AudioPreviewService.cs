using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Mp3ClipEditorTagger.Services;

public sealed class AudioPreviewService : IDisposable
{
    private WaveOutEvent? _outputDevice;
    private AudioFileReader? _audioReader;
    private AdjustableGainSampleProvider? _gainProvider;
    private bool _isStopping;

    public event EventHandler? PlaybackStopped;

    public bool IsPlaying => _outputDevice?.PlaybackState == PlaybackState.Playing;

    public double CurrentTimeSeconds => _audioReader?.CurrentTime.TotalSeconds ?? 0d;

    public void Start(string filePath, double startSeconds, float initialGain)
    {
        Stop();

        _audioReader = new AudioFileReader(filePath)
        {
            CurrentTime = TimeSpan.FromSeconds(Math.Max(0d, startSeconds))
        };

        _gainProvider = new AdjustableGainSampleProvider(_audioReader)
        {
            Gain = initialGain
        };

        _outputDevice = new WaveOutEvent();
        _outputDevice.PlaybackStopped += OutputDevice_PlaybackStopped;
        _outputDevice.Init(_gainProvider);
        _outputDevice.Play();
    }

    public void SetGain(float gain)
    {
        if (_gainProvider is not null)
        {
            _gainProvider.Gain = gain;
        }
    }

    public void Stop()
    {
        _isStopping = true;

        if (_outputDevice is not null)
        {
            _outputDevice.PlaybackStopped -= OutputDevice_PlaybackStopped;
            try
            {
                _outputDevice.Stop();
            }
            catch
            {
            }

            _outputDevice.Dispose();
            _outputDevice = null;
        }

        _audioReader?.Dispose();
        _audioReader = null;
        _gainProvider = null;

        _isStopping = false;
    }

    public void Dispose()
    {
        Stop();
        GC.SuppressFinalize(this);
    }

    private void OutputDevice_PlaybackStopped(object? sender, StoppedEventArgs e)
    {
        if (_isStopping)
        {
            return;
        }

        PlaybackStopped?.Invoke(this, EventArgs.Empty);
    }

    private sealed class AdjustableGainSampleProvider(ISampleProvider source) : ISampleProvider
    {
        public float Gain { get; set; } = 1f;

        public WaveFormat WaveFormat => source.WaveFormat;

        public int Read(float[] buffer, int offset, int count)
        {
            var read = source.Read(buffer, offset, count);
            if (Math.Abs(Gain - 1f) < 0.0001f)
            {
                return read;
            }

            for (var index = 0; index < read; index++)
            {
                buffer[offset + index] *= Gain;
            }

            return read;
        }
    }
}

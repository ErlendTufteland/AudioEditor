using System.Windows.Threading;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace AudioEditor.App.Services;

/// <summary>Plays one sample provider at a time on the default output device.</summary>
public sealed class PlaybackService : IDisposable
{
    private readonly Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
    private WaveOut? player;
    private GuardedSampleProvider? source;
    private int bytesPerFrame;
    private int sampleRate;

    /// <summary>Raised on the UI thread when playback reaches the end or fails.</summary>
    public event EventHandler<Exception?>? Ended;

    public bool IsPlaying => player is not null;

    /// <summary>How much has been heard since <see cref="Play"/> was called.</summary>
    public TimeSpan Position
    {
        get
        {
            if (player is null || bytesPerFrame == 0)
                return TimeSpan.Zero;
            long frames = player.GetPosition() / bytesPerFrame;
            return TimeSpan.FromSeconds((double)frames / sampleRate);
        }
    }

    /// <summary>
    /// Starts playing <paramref name="provider"/>, stopping anything that was playing.
    /// Takes ownership of the provider and disposes it when playback ends.
    /// </summary>
    public void Play(ISampleProvider provider)
    {
        Stop();

        var guarded = new GuardedSampleProvider(provider);
        var output = new SampleToWaveProvider16(guarded);
        var device = new WaveOut { BufferMilliseconds = 60, NumberOfBuffers = 3 };
        device.PlaybackStopped += (_, e) => OnPlaybackStopped(device, guarded, e.Exception);
        try
        {
            device.Init(output);
            device.Play();
        }
        catch
        {
            device.Dispose();
            Release(guarded);
            throw;
        }

        player = device;
        source = guarded;
        bytesPerFrame = output.WaveFormat.BlockAlign;
        sampleRate = output.WaveFormat.SampleRate;
    }

    public void Stop()
    {
        var device = player;
        var playing = source;
        player = null;
        source = null;
        if (device is null)
            return;

        device.Stop();
        device.Dispose();
        if (playing is not null)
            Release(playing);
    }

    public void Dispose() => Stop();

    private void OnPlaybackStopped(WaveOut device, GuardedSampleProvider playing, Exception? error)
    {
        if (!dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(() => OnPlaybackStopped(device, playing, error));
            return;
        }

        if (!ReferenceEquals(device, player))
            return; // Stopped by Stop(), which has already cleaned up.

        player = null;
        source = null;
        device.Dispose();
        Release(playing);
        Ended?.Invoke(this, error);
    }

    // Files are opened on the audio thread, so close them off the UI thread as well.
    private static void Release(GuardedSampleProvider provider) => Task.Run(provider.Dispose);

    /// <summary>Lets the provider be disposed safely while the audio thread may still read from it.</summary>
    private sealed class GuardedSampleProvider(ISampleProvider inner) : ISampleProvider, IDisposable
    {
        private readonly Lock gate = new();
        private bool disposed;

        public WaveFormat WaveFormat => inner.WaveFormat;

        public int Read(Span<float> buffer)
        {
            lock (gate)
                return disposed ? 0 : inner.Read(buffer);
        }

        public void Dispose()
        {
            lock (gate)
            {
                if (disposed)
                    return;
                disposed = true;
                (inner as IDisposable)?.Dispose();
            }
        }
    }
}

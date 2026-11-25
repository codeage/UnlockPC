using System.Threading.Channels;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Media.SpeechSynthesis;

namespace UnlockPC;

internal sealed class LockActivitySpeech : IDisposable
{
    private readonly Channel<string> _latest = Channel.CreateBounded<string>(new BoundedChannelOptions(1)
    {
        SingleReader = true,
        SingleWriter = true,
        FullMode = BoundedChannelFullMode.DropOldest
    });
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _worker;

    public LockActivitySpeech()
    {
        var token = _stop.Token;
        _worker = Task.Run(() => SpeakLatestAsync(token));
    }

    public void SpeakLatest(string message) => _latest.Writer.TryWrite(message);

    private async Task SpeakLatestAsync(CancellationToken token)
    {
        try
        {
            var voices = SpeechSynthesizer.AllVoices;
            var voice = voices.FirstOrDefault(item => item.Language.Equals("zh-CN", StringComparison.OrdinalIgnoreCase)) ??
                voices.FirstOrDefault(item => item.Language.StartsWith("zh-", StringComparison.OrdinalIgnoreCase));

            while (await _latest.Reader.WaitToReadAsync(token).ConfigureAwait(false))
            {
                if (!_latest.Reader.TryRead(out string? message))
                    continue;

                var next = _latest.Reader.WaitToReadAsync(token).AsTask();
                if (next.IsCompletedSuccessfully && next.Result)
                    continue;

                using var playback = CancellationTokenSource.CreateLinkedTokenSource(token);
                Task current = PlayAsync(message, voice, playback.Token);
                if (await Task.WhenAny(current, next).ConfigureAwait(false) == next && !current.IsCompleted)
                    playback.Cancel();
                await current.ConfigureAwait(false);
                if (!await next.ConfigureAwait(false))
                    break;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            BluetoothLog.Write("lock_activity_speech_failed", new { ex.HResult });
        }
    }

    private static async Task PlayAsync(string message, VoiceInformation? voice, CancellationToken token)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            using var synthesizer = new SpeechSynthesizer();
            if (voice is not null)
                synthesizer.Voice = voice;
            using var stream = await synthesizer.SynthesizeTextToStreamAsync(message).AsTask(token).WaitAsync(token).ConfigureAwait(false);
            using var source = MediaSource.CreateFromStream(stream, stream.ContentType);
            using var player = new MediaPlayer { AudioCategory = MediaPlayerAudioCategory.Speech };
            player.CommandManager.IsEnabled = false;

            var ended = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            player.MediaEnded += (_, _) => ended.TrySetResult(true);
            player.MediaFailed += (_, _) => ended.TrySetResult(false);
            player.Source = source;
            token.ThrowIfCancellationRequested();
            player.Play();
            BluetoothLog.Write("lock_activity_speech_started");
            bool played = await ended.Task.WaitAsync(TimeSpan.FromSeconds(20), token).ConfigureAwait(false);
            BluetoothLog.Write(played ? "lock_activity_speech_played" : "lock_activity_speech_failed");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            BluetoothLog.Write("lock_activity_speech_interrupted");
        }
        catch (Exception ex)
        {
            BluetoothLog.Write("lock_activity_speech_failed", new { ex.HResult });
        }
    }

    public void Dispose()
    {
        if (!_latest.Writer.TryComplete())
            return;
        _stop.Cancel();
        _ = _worker.ContinueWith(_ => _stop.Dispose(), CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
}

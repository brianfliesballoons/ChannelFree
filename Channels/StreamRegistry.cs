using System.Collections.Concurrent;

namespace Jellyfin.Plugin.QuasiTV.Channels;

/// <summary>A live channel stream the plugin is currently feeding.</summary>
public sealed class ActiveStream : IDisposable
{
    private readonly CancellationTokenSource _session = new();

    /// <summary>Cancelled when a newer tune-in for the same channel replaces this one.</summary>
    public CancellationToken Replaced => _session.Token;

    /// <summary>The ffmpeg run in progress; cancelling it makes the loop re-read the schedule (Restart/Skip).</summary>
    public CancellationTokenSource? CurrentRun { get; set; }

    public void Replace() => _session.Cancel();

    public void Dispose() => _session.Dispose();
}

/// <summary>At most one plugin stream per channel. Stops leaked encoders; lets Restart/Skip reach the running stream.</summary>
public static class StreamRegistry
{
    private static readonly ConcurrentDictionary<string, ActiveStream> Active = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>True while any channel is being watched.</summary>
    public static bool AnyActive => !Active.IsEmpty;

    /// <summary>Register a new stream, stopping any older one on the same channel.</summary>
    public static ActiveStream Take(string channelId)
    {
        var stream = new ActiveStream();
        ActiveStream? old = null;
        Active.AddOrUpdate(
            channelId,
            stream,
            (_, existing) =>
            {
                old = existing;
                return stream;
            });

        try
        {
            old?.Replace();
        }
        catch (ObjectDisposedException)
        {
            // already finished
        }

        return stream;
    }

    public static void Release(string channelId, ActiveStream stream)
    {
        Active.TryRemove(new KeyValuePair<string, ActiveStream>(channelId, stream));
    }

    /// <summary>Tell a running stream to jump to whatever the schedule now says.</summary>
    public static bool Nudge(string channelId)
    {
        if (Active.TryGetValue(channelId, out var stream) && stream.CurrentRun is { } run)
        {
            try
            {
                run.Cancel();
                return true;
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
        }

        return false;
    }
}

namespace Jellyfin.Plugin.QuasiTV.Channels;

/// <summary>
/// "Record" in the guide = jump. Remembers the last jump per channel so that backing out of the
/// channel soon afterwards puts it back to the start of what was on before.
/// The decision is made when the viewer comes back: a quick return (reload / reconnect) keeps the jump.
/// </summary>
public static class JumpManager
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, PendingJump> Pending = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Streams shorter than this don't count as "watching" (Jellyfin probes the stream briefly on tune-in).</summary>
    public static readonly TimeSpan MinWatch = TimeSpan.FromSeconds(10);

    /// <summary>Coming back within this long after leaving = a reload/reconnect, not "backing out".</summary>
    public static readonly TimeSpan ReloadGrace = TimeSpan.FromSeconds(20);

    private static TimeSpan Window => TimeSpan.FromMinutes(Math.Clamp(Plugin.Instance!.Configuration.RevertWindowMinutes, 0, 240));

    /// <summary>Make the program starting at <paramref name="targetStartUtc"/> start now. Remembers what was on for revert.</summary>
    public static void JumpTo(QuasiChannel channel, DateTime targetStartUtc)
    {
        var now = DateTime.UtcNow;
        var current = ChannelScheduler.Current(channel, now);
        lock (Gate)
        {
            if (current is not null)
            {
                Pending[channel.Id] = new PendingJump(now, ChannelScheduler.ToScheduleTime(channel, current.StartUtc));
            }
        }

        ChannelScheduler.MoveToNow(channel, targetStartUtc);
    }

    /// <summary>A viewer's stream ended (not replaced by a newer tune-in). Just note the time; decide on return.</summary>
    public static void OnViewerLeft(QuasiChannel channel, TimeSpan watched)
    {
        if (watched < MinWatch)
        {
            return;
        }

        lock (Gate)
        {
            if (Pending.TryGetValue(channel.Id, out var jump) && jump.LeftAtUtc is null)
            {
                var now = DateTime.UtcNow;
                if (now - jump.JumpedAtUtc > Window)
                {
                    Pending.Remove(channel.Id); // watched long enough: the jump stuck
                }
                else
                {
                    jump.LeftAtUtc = now;
                }
            }
        }
    }

    /// <summary>Someone is tuning in. Returns true if the channel was reverted to the pre-jump program.</summary>
    public static bool OnTuneIn(QuasiChannel channel)
    {
        PendingJump? jump;
        var now = DateTime.UtcNow;
        lock (Gate)
        {
            if (!Pending.TryGetValue(channel.Id, out jump))
            {
                return false;
            }

            if (jump.LeftAtUtc is not DateTime left)
            {
                // Still watching (or never left); drop it once the window has passed.
                if (now - jump.JumpedAtUtc > Window)
                {
                    Pending.Remove(channel.Id);
                }

                return false;
            }

            if (now - left < ReloadGrace)
            {
                jump.LeftAtUtc = null; // reload / reconnect: keep the jump
                return false;
            }

            Pending.Remove(channel.Id);
        }

        ChannelScheduler.MoveScheduleTimeToNow(channel, jump.PreviousScheduleStart);
        return true;
    }

    /// <summary>Forget a pending revert (e.g. the channel was restarted instead).</summary>
    public static void Clear(string channelId)
    {
        lock (Gate)
        {
            Pending.Remove(channelId);
        }
    }

    private sealed class PendingJump
    {
        public PendingJump(DateTime jumpedAtUtc, DateTime previousScheduleStart)
        {
            JumpedAtUtc = jumpedAtUtc;
            PreviousScheduleStart = previousScheduleStart;
        }

        public DateTime JumpedAtUtc { get; }

        public DateTime PreviousScheduleStart { get; }

        public DateTime? LeftAtUtc { get; set; }
    }
}

using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.QuasiTV.Channels;

/// <summary>
/// After a jump/restart, make Jellyfin re-read the guide: delete its cached XMLTV copy
/// (kept up to 1 h) and run the "Refresh Guide" task. Debounced so several presses = one refresh.
/// </summary>
public static class GuideRefresher
{
    private static readonly TimeSpan Debounce = TimeSpan.FromSeconds(3);
    private static readonly object Gate = new();
    private static ITaskManager? _tasks;
    private static string? _xmltvCacheDir;
    private static ILogger? _logger;
    private static ILibraryManager? _library;
    private static CancellationTokenSource? _pending;

    public static void Init(ITaskManager tasks, string cachePath, ILogger logger, ILibraryManager library)
    {
        _library = library;
        _tasks = tasks;
        _xmltvCacheDir = Path.Combine(cachePath, "xmltv");
        _logger = logger;
    }

    private static bool _pendingUrgent;

    // What the settings page shows: idle | pending | waiting | running
    private static volatile string _phase = "idle";
    private static DateTime? _lastDoneUtc;

    /// <summary>Snapshot for the settings page's guide-status strip.</summary>
    public static object Status()
    {
        var worker = _tasks?.ScheduledTasks.FirstOrDefault(t => t.ScheduledTask.Key == "RefreshGuide");
        var running = worker is not null && worker.State == TaskState.Running;
        var phase = running ? "running" : _phase;
        var last = worker?.LastExecutionResult;
        return new
        {
            phase,
            progress = running ? worker!.CurrentProgress : null,
            anyoneWatching = StreamRegistry.AnyActive,
            lastEndUtc = last?.EndTimeUtc ?? _lastDoneUtc,
            lastStatus = last?.Status.ToString(),
            lastError = last?.ErrorMessage
        };
    }

    /// <summary>
    /// Refresh the guide soon. <paramref name="waitUntilIdle"/> = automatic refresh: hold it until nobody is watching,
    /// because a guide refresh makes TVs silently reconnect (the picture jumps to live).
    /// </summary>
    public static void Request(bool waitUntilIdle = false)
    {
        CancellationTokenSource cts;
        lock (Gate)
        {
            if (waitUntilIdle && _pending is not null && !_pending.IsCancellationRequested && _pendingUrgent)
            {
                return; // an immediate refresh is already coming; it'll include this change
            }

            _pending?.Cancel();
            _pending = cts = new CancellationTokenSource();
            _pendingUrgent = !waitUntilIdle;
            _phase = "pending";
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(Debounce, cts.Token).ConfigureAwait(false);
                if (waitUntilIdle)
                {
                    if (StreamRegistry.AnyActive)
                    {
                        _phase = "waiting";
                    }

                    var waited = TimeSpan.Zero;
                    while (StreamRegistry.AnyActive && waited < TimeSpan.FromHours(8))
                    {
                        await Task.Delay(TimeSpan.FromSeconds(30), cts.Token).ConfigureAwait(false);
                        waited += TimeSpan.FromSeconds(30);
                    }

                    // Give a channel-surfer a moment before refreshing.
                    await Task.Delay(TimeSpan.FromSeconds(20), cts.Token).ConfigureAwait(false);
                    if (StreamRegistry.AnyActive)
                    {
                        Request(waitUntilIdle: true); // someone tuned back in; try again later
                        return;
                    }
                }

                _phase = "running";
                await Run().ConfigureAwait(false);
                _lastDoneUtc = DateTime.UtcNow;
                _phase = "idle";
            }
            catch (OperationCanceledException)
            {
                // superseded by a newer request (which set its own phase)
            }
            catch (Exception ex)
            {
                _phase = "idle";
                _logger?.LogWarning(ex, "QuasiTV: guide refresh failed");
            }
        });
    }

    private static readonly SemaphoreSlim RunLock = new(1, 1);

    private static async Task Run()
    {
        if (_tasks is null)
        {
            return;
        }

        await RunLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await RunInner().ConfigureAwait(false);
        }
        finally
        {
            RunLock.Release();
        }
    }

    private static async Task RunInner()
    {
        if (_tasks is null)
        {
            return;
        }

        var worker = _tasks.ScheduledTasks.FirstOrDefault(t => t.ScheduledTask.Key == "RefreshGuide");
        if (worker is null)
        {
            _logger?.LogWarning("QuasiTV: couldn't find the Refresh Guide task");
            return;
        }

        // Never cancel a refresh halfway (it would skip Jellyfin's cleanup of old programs and leave overlaps).
        // Wait for it to finish, then run once more with the latest schedule.
        var waited = TimeSpan.Zero;
        while (worker.State != TaskState.Idle && waited < TimeSpan.FromMinutes(5))
        {
            await Task.Delay(2000).ConfigureAwait(false);
            waited += TimeSpan.FromSeconds(2);
        }

        if (_xmltvCacheDir is not null && Directory.Exists(_xmltvCacheDir))
        {
            foreach (var f in Directory.GetFiles(_xmltvCacheDir))
            {
                try
                {
                    File.Delete(f);
                }
                catch (IOException)
                {
                    // in use; next refresh will get it
                }
            }
        }

        _logger?.LogInformation("QuasiTV: refreshing the guide");
        await _tasks.Execute(worker, new TaskOptions()).ConfigureAwait(false);

        try
        {
            RemoveStalePrograms();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "QuasiTV: stale-program cleanup failed");
        }
    }

    /// <summary>
    /// Self-heal for overlapping guide entries: Jellyfin skips its own cleanup of old programs if anything in
    /// Live TV errors during a refresh, so a rebuilt schedule can end up stacked on the old one. After each refresh,
    /// delete any stored program on one of our channels that isn't in our current schedule.
    /// </summary>
    private static void RemoveStalePrograms()
    {
        if (_library is null)
        {
            return;
        }

        var now = DateTime.UtcNow;
        var ours = ChannelScheduler.GetChannels(_library).ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);
        if (ours.Count == 0)
        {
            return;
        }

        var startsByChannel = new Dictionary<string, HashSet<long>>(StringComparer.OrdinalIgnoreCase);
        foreach (var ch in ours.Values)
        {
            startsByChannel[ch.Name] = ChannelScheduler.Slots(ch, now.AddHours(-6), now.AddDays(3))
                .Select(sl => sl.StartUtc.Ticks / TimeSpan.TicksPerSecond)
                .ToHashSet();
        }

        var programs = _library.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.LiveTvProgram },
            MinEndDate = now
        }).OfType<LiveTvProgram>().ToList();

        var channelNames = new Dictionary<Guid, string?>();
        var removed = 0;
        foreach (var p in programs)
        {
            if (!channelNames.TryGetValue(p.ChannelId, out var chName))
            {
                chName = (_library.GetItemById(p.ChannelId) as LiveTvChannel)?.Name;
                channelNames[p.ChannelId] = chName;
            }

            if (chName is null || !startsByChannel.TryGetValue(chName, out var starts))
            {
                continue; // not one of our channels
            }

            var start = DateTime.SpecifyKind(p.StartDate, DateTimeKind.Utc).Ticks / TimeSpan.TicksPerSecond;
            if (starts.Contains(start) || starts.Contains(start - 1) || starts.Contains(start + 1))
            {
                continue; // still on the schedule
            }

            _library.DeleteItem(p, new DeleteOptions { DeleteFileLocation = false });
            removed++;
        }

        if (removed > 0)
        {
            _logger?.LogInformation("QuasiTV: removed {Count} stale guide entries left over from an earlier schedule", removed);
        }
    }
}

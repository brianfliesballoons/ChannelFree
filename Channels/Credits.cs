using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Chapters;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.MediaSegments;
using MediaBrowser.Model.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.QuasiTV.Channels;

/// <summary>
/// Where end credits start, and how much of an item to actually play.
/// Sources: Jellyfin media segments of type Outro (written by Intro Skipper), else a chapter named like "Credits".
/// Only trims when the marker looks like real end credits (near the end), so shows whose credits roll
/// over the final scene without a marker are untouched. Per-show / per-movie "Leave alone" + tag cable:keep-credits.
/// </summary>
public static partial class Credits
{
    private static readonly ConcurrentDictionary<Guid, (DateTime At, long? Start)> Cache = new();
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(30);

    private static IMediaSegmentManager? _segments;
    private static IChapterManager? _chapters;
    private static ILogger? _logger;

    public static void Init(IMediaSegmentManager segments, IChapterManager chapters, ILogger logger)
    {
        _segments = segments;
        _chapters = chapters;
        _logger = logger;
    }

    [GeneratedRegex(@"\b(end\s*)?credits?\b|\bclosing\b|\boutro\b", RegexOptions.IgnoreCase)]
    private static partial Regex CreditsName();

    /// <summary>Ticks of the item to play (runtime, or credits start + keep). Also returns whether it was trimmed.</summary>
    public static long PlayTicks(BaseItem item, long runtimeTicks, out bool trimmed)
    {
        trimmed = false;
        var cfg = Plugin.Instance!.Configuration;
        if (!cfg.TrimCredits || runtimeTicks <= 0 || KeepsCredits(item, cfg))
        {
            return runtimeTicks;
        }

        var start = CreditsStart(item);
        if (start is not long s)
        {
            return runtimeTicks;
        }

        var isMovie = item is Movie;
        var creditsLen = runtimeTicks - s;

        // Sanity: real end credits start in the last part of the runtime.
        var maxCredits = isMovie ? TimeSpan.FromMinutes(15) : TimeSpan.FromMinutes(4);
        if (creditsLen <= 0 || creditsLen > maxCredits.Ticks || s < runtimeTicks * 0.6)
        {
            return runtimeTicks;
        }

        var keep = TimeSpan.FromSeconds(Math.Clamp(isMovie ? cfg.CreditsKeepSecondsMovie : cfg.CreditsKeepSecondsTv, 0, 600)).Ticks;
        var play = s + keep;
        if (play >= runtimeTicks - TimeSpan.FromSeconds(5).Ticks)
        {
            return runtimeTicks; // nothing worth cutting
        }

        trimmed = true;
        return play;
    }

    private static bool KeepsCredits(BaseItem item, Configuration.PluginConfiguration cfg)
    {
        var owner = item is Episode ep && ep.Series is not null ? (BaseItem)ep.Series : item;
        if (cfg.KeepCredits.Contains(owner.Id) || cfg.KeepCredits.Contains(item.Id))
        {
            return true;
        }

        static bool Tagged(BaseItem i) => i.Tags?.Any(t => string.Equals(t, "cable:keep-credits", StringComparison.OrdinalIgnoreCase)) == true;
        return Tagged(item) || Tagged(owner);
    }

    /// <summary>Credits start in ticks from the item start, or null if unknown.</summary>
    public static long? CreditsStart(BaseItem item)
    {
        if (Cache.TryGetValue(item.Id, out var c) && DateTime.UtcNow - c.At < CacheFor)
        {
            return c.Start;
        }

        long? start = null;
        try
        {
            if (_segments is not null)
            {
                var segs = _segments.GetSegmentsAsync(item, new[] { MediaSegmentType.Outro }, new LibraryOptions(), false)
                    .GetAwaiter().GetResult();
                var outro = segs?.Where(x => x.Type == MediaSegmentType.Outro).OrderByDescending(x => x.StartTicks).FirstOrDefault();
                if (outro is not null && outro.StartTicks > 0)
                {
                    start = outro.StartTicks;
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "QuasiTV: media segments lookup failed for {Item}", item.Name);
        }

        if (start is null)
        {
            try
            {
                var chapters = _chapters?.GetChapters(item.Id);
                var ch = chapters?.Where(x => x.StartPositionTicks > 0 && !string.IsNullOrWhiteSpace(x.Name) && CreditsName().IsMatch(x.Name!))
                    .OrderByDescending(x => x.StartPositionTicks).FirstOrDefault();
                if (ch is not null)
                {
                    start = ch.StartPositionTicks;
                }
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "QuasiTV: chapter lookup failed for {Item}", item.Name);
            }
        }

        Cache[item.Id] = (DateTime.UtcNow, start);
        return start;
    }

    public static void ClearCache() => Cache.Clear();
}

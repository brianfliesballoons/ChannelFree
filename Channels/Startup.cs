using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Chapters;
using MediaBrowser.Controller.MediaSegments;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.QuasiTV.Channels;

/// <summary>
/// Startup work: hands the credits lookup its services, and adds the Channel Free helper script
/// (down-arrow info banner) to Jellyfin's web client — used by browsers and the LG app.
/// Jellyfin image updates replace the web files, so this runs on every start.
/// </summary>
public sealed class Startup : IHostedService
{
    private const string Marker = "<!-- cable-package -->";
    private const string Tag = Marker + "<script defer src=\"../QuasiTV/client.js\"></script>";

    private readonly IApplicationPaths _paths;
    private readonly ILogger<Startup> _logger;

    public Startup(IMediaSegmentManager segments, IChapterManager chapters, IApplicationPaths paths, ILogger<Startup> logger)
    {
        _paths = paths;
        _logger = logger;
        Credits.Init(segments, chapters, logger);
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        UpdateWebClient(Plugin.Instance?.Configuration.DownArrowBanner ?? true);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Add (or remove) the script tag in the web client's index.html.</summary>
    public void UpdateWebClient(bool enabled) => Apply(_paths.WebPath, enabled, _logger);

    public static void Apply(string? webPath, bool enabled, ILogger logger)
    {
        try
        {
            if (string.IsNullOrEmpty(webPath))
            {
                return;
            }

            var index = Path.Combine(webPath, "index.html");
            if (!File.Exists(index))
            {
                logger.LogInformation("QuasiTV: web client not found at {Path}; down-arrow banner unavailable", webPath);
                return;
            }

            var html = File.ReadAllText(index);
            var has = html.Contains(Marker, StringComparison.Ordinal);
            string updated;
            if (enabled && !has)
            {
                var at = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
                updated = at >= 0 ? html.Insert(at, Tag) : html + Tag;
            }
            else if (!enabled && has)
            {
                var start = html.IndexOf(Marker, StringComparison.Ordinal);
                var end = html.IndexOf("</script>", start, StringComparison.Ordinal);
                updated = end > start ? html.Remove(start, end + "</script>".Length - start) : html;
            }
            else
            {
                return;
            }

            File.WriteAllText(index, updated);
            logger.LogInformation("QuasiTV: down-arrow banner script {State} the web client", enabled ? "added to" : "removed from");
        }
        catch (UnauthorizedAccessException)
        {
            logger.LogWarning("QuasiTV: no permission to edit {Path}/index.html, so the web/LG down-arrow banner is off. (Jellyfin's container user can't write its web folder.)", webPath);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "QuasiTV: couldn't update the web client for the down-arrow banner");
        }
    }
}

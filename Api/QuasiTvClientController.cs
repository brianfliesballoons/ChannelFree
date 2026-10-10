using System.Globalization;
using Jellyfin.Plugin.QuasiTV.Channels;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.QuasiTV.Api;

/// <summary>Helper script for the web client / LG app (down-arrow info banner) and the data it shows.</summary>
[ApiController]
[Route("QuasiTV")]
public class QuasiTvClientController : ControllerBase
{
    private readonly ILibraryManager _library;

    public QuasiTvClientController(ILibraryManager library)
    {
        _library = library;
    }

    /// <summary>The script injected into the web client.</summary>
    [HttpGet("client.js")]
    [AllowAnonymous]
    public IActionResult ClientScript()
    {
        var asm = typeof(Plugin).Assembly;
        var res = asm.GetManifestResourceNames().FirstOrDefault(r => r.EndsWith(".client.js", StringComparison.OrdinalIgnoreCase));
        if (res is null || !(Plugin.Instance?.Configuration.DownArrowBanner ?? false))
        {
            return Content("/* Channel Free: down-arrow banner off */", "application/javascript");
        }

        using var s = asm.GetManifestResourceStream(res)!;
        using var r = new StreamReader(s);
        Response.Headers.CacheControl = "no-cache";
        return Content(r.ReadToEnd(), "application/javascript");
    }

    /// <summary>What's on a Channel Free channel right now (matched by channel number or name as Jellyfin shows it).</summary>
    [HttpGet("client/now")]
    [Authorize]
    public IActionResult Now([FromQuery] string? number, [FromQuery] string? name)
    {
        var channels = ChannelScheduler.GetChannels(_library);
        QuasiChannel? ch = null;
        if (!string.IsNullOrWhiteSpace(number) && int.TryParse(number.Split('.')[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
        {
            ch = channels.FirstOrDefault(c => c.Number == n && (name is null || string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)))
                 ?? channels.FirstOrDefault(c => c.Number == n);
        }

        ch ??= channels.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
        if (ch is null)
        {
            return Ok(new { cable = false });
        }

        var now = DateTime.UtcNow;
        var slots = ChannelScheduler.Slots(ch, now, now.AddHours(6)).Take(2).ToList();
        var cur = slots.ElementAtOrDefault(0);
        var next = slots.ElementAtOrDefault(1);
        if (cur is null)
        {
            return Ok(new { cable = true, number = ch.Number, channel = ch.Name });
        }

        string title = cur.Item.Name ?? string.Empty;
        string? sub = null;
        if (cur.Item is Episode ep)
        {
            title = ep.SeriesName ?? ep.Series?.Name ?? title;
            var num = ep.IndexNumber is int e ? $"S{ep.ParentIndexNumber ?? 1} E{e}" : null;
            sub = string.Join(" · ", new[] { num, ep.Name }.Where(x => !string.IsNullOrWhiteSpace(x)));
        }
        else if (cur.Item.ProductionYear is int y)
        {
            sub = y.ToString(CultureInfo.InvariantCulture);
        }

        string? nextTitle = next?.Item is Episode nep ? (nep.SeriesName ?? nep.Series?.Name) : next?.Item.Name;
        return Ok(new
        {
            cable = true,
            number = ch.Number,
            channel = ch.Name,
            title,
            sub,
            startUtc = cur.StartUtc,
            endUtc = cur.EndUtc,
            nowUtc = now,
            next = nextTitle
        });
    }
}

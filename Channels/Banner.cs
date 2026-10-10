using System.Globalization;
using MediaBrowser.Controller.Entities.TV;

namespace Jellyfin.Plugin.QuasiTV.Channels;

/// <summary>
/// Cable-box style info banner burned into the first few seconds after tuning in:
/// channel number + name, title, air times, minutes left and a progress bar.
/// (TV apps can't show live-program progress themselves, so we draw it into the picture.)
/// </summary>
public static class Banner
{
    private static readonly object Gate = new();
    private static string? _fontDir;

    /// <summary>Returns the extra filter chain (starting with ",") or "" if the banner is off / fonts unavailable.
    /// Writes the text into temp files in <paramref name="workDir"/> (avoids ffmpeg escaping problems).</summary>
    public static string Filter(QuasiChannel channel, Slot slot, DateTime nowUtc, string workDir)
    {
        var cfg = Plugin.Instance!.Configuration;
        if (!cfg.ShowBanner)
        {
            return string.Empty;
        }

        var fonts = FontDir();
        if (fonts is null)
        {
            return string.Empty;
        }

        var tz = ResolveTimeZone(cfg.TimeZoneId);
        var start = TimeZoneInfo.ConvertTimeFromUtc(slot.StartUtc, tz);
        var end = TimeZoneInfo.ConvertTimeFromUtc(slot.EndUtc, tz);
        var total = (slot.EndUtc - slot.StartUtc).TotalSeconds;
        var done = Math.Clamp((nowUtc - slot.StartUtc).TotalSeconds / Math.Max(1, total), 0, 1);
        var left = Math.Max(1, (int)Math.Ceiling((slot.EndUtc - nowUtc).TotalMinutes));

        var line1 = $"{channel.Number}  ·  {channel.Name.ToUpperInvariant()}";
        string line2;
        if (slot.Item is Episode ep)
        {
            var num = ep.IndexNumber is int e ? $"S{ep.ParentIndexNumber ?? 1}E{e} · " : string.Empty;
            line2 = $"{ep.SeriesName} — {num}{ep.Name}";
        }
        else
        {
            line2 = slot.Item.ProductionYear is int y ? $"{slot.Item.Name} ({y})" : slot.Item.Name ?? string.Empty;
        }

        var inv = CultureInfo.GetCultureInfo("en-US");
        var line3 = $"{start.ToString("h:mm", inv)} – {end.ToString("h:mm tt", inv)}   ·   {left} min left";

        Directory.CreateDirectory(workDir);
        var f1 = Path.Combine(workDir, "l1.txt");
        var f2 = Path.Combine(workDir, "l2.txt");
        var f3 = Path.Combine(workDir, "l3.txt");
        File.WriteAllText(f1, line1);
        File.WriteAllText(f2, line2.Length > 70 ? line2[..67] + "…" : line2);
        File.WriteAllText(f3, line3);

        var secs = Math.Clamp(cfg.BannerSeconds, 1, 30).ToString(CultureInfo.InvariantCulture);
        var on = $":enable='lt(t,{secs})'";
        var bold = Path.Combine(fonts, "DejaVuSans-Bold.ttf");
        var regular = Path.Combine(fonts, "DejaVuSans.ttf");
        var frac = done.ToString("0.0000", CultureInfo.InvariantCulture);

        return
            $",drawbox=x=0:y=ih-ih*0.21:w=iw:h=ih*0.21:color=black@0.78:t=fill{on}" +
            $",drawbox=x=0:y=ih-ih*0.21:w=iw:h=3:color=0x00A4DC@1:t=fill{on}" +
            $",drawtext=fontfile={bold}:textfile={f1}:expansion=none:fontcolor=0x00A4DC:fontsize=h*0.030:x=w*0.03:y=h-h*0.19{on}" +
            $",drawtext=fontfile={bold}:textfile={f2}:expansion=none:fontcolor=white:fontsize=h*0.048:x=w*0.03:y=h-h*0.145{on}" +
            $",drawtext=fontfile={regular}:textfile={f3}:expansion=none:fontcolor=white@0.85:fontsize=h*0.030:x=w*0.03:y=h-h*0.083{on}" +
            $",drawbox=x=iw*0.03:y=ih-ih*0.037:w=iw*0.94:h=ih*0.011:color=white@0.25:t=fill{on}" +
            $",drawbox=x=iw*0.03:y=ih-ih*0.037:w=max(2\\,iw*0.94*{frac}):h=ih*0.011:color=0x00A4DC@1:t=fill{on}";
    }

    internal static TimeZoneInfo ResolveTimeZone(string? id)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(id))
            {
                return TimeZoneInfo.FindSystemTimeZoneById(id);
            }
        }
        catch (Exception)
        {
            // fall through
        }

        return TimeZoneInfo.Local;
    }

    /// <summary>Extract the bundled DejaVu fonts next to the plugin settings (once).</summary>
    internal static string? FontDir()
    {
        lock (Gate)
        {
            if (_fontDir is not null)
            {
                return _fontDir;
            }

            try
            {
                var dir = Path.Combine(ChannelScheduler.BaseDir(), "fonts");
                Directory.CreateDirectory(dir);
                var asm = typeof(Plugin).Assembly;
                foreach (var name in new[] { "DejaVuSans.ttf", "DejaVuSans-Bold.ttf" })
                {
                    var target = Path.Combine(dir, name);
                    if (File.Exists(target))
                    {
                        continue;
                    }

                    var res = asm.GetManifestResourceNames().FirstOrDefault(r => r.EndsWith("." + name, StringComparison.OrdinalIgnoreCase));
                    if (res is null)
                    {
                        return null;
                    }

                    using var src = asm.GetManifestResourceStream(res)!;
                    using var dst = File.Create(target);
                    src.CopyTo(dst);
                }

                _fontDir = dir;
                return dir;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace AirPlay.Core.Video;

/// <summary>One "#EXT-X-STREAM-INF" variant of a master playlist.</summary>
public sealed record HlsVariant(string Uri, long Bandwidth, int Width, int Height, string Codecs, string? AudioGroup, string InfLine);

/// <summary>One "#EXT-X-MEDIA:TYPE=AUDIO" rendition of a master playlist.</summary>
public sealed record HlsAudio(string Uri, string GroupId, string? Language, bool IsDefault, string Line);

/// <summary>
/// HLS playlist helpers for AirPlay video (YouTube sends its playlists through the iPhone).
/// </summary>
public static partial class HlsPlaylist
{
    [GeneratedRegex("""([A-Z0-9-]+)=("[^"]*"|[^,]*)""")]
    private static partial Regex AttributeRegex();

    public static Dictionary<string, string> Attributes(string tagLine)
    {
        var colon = tagLine.IndexOf(':');
        var result = new Dictionary<string, string>();
        if (colon < 0) return result;
        foreach (Match m in AttributeRegex().Matches(tagLine[(colon + 1)..]))
            result[m.Groups[1].Value] = m.Groups[2].Value.Trim('"');
        return result;
    }

    public static (List<HlsVariant> Variants, List<HlsAudio> Audio) ParseMaster(string master)
    {
        var variants = new List<HlsVariant>();
        var audio = new List<HlsAudio>();
        var lines = master.Replace("\r", "").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.StartsWith("#EXT-X-STREAM-INF:"))
            {
                var a = Attributes(line);
                var uri = lines.Skip(i + 1).Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0 && !l.StartsWith('#'));
                if (uri is null) continue;
                a.TryGetValue("RESOLUTION", out var res);
                int w = 0, h = 0;
                if (res?.Split('x') is [var ws, var hs]) { int.TryParse(ws, out w); int.TryParse(hs, out h); }
                variants.Add(new HlsVariant(uri,
                    a.TryGetValue("BANDWIDTH", out var bw) && long.TryParse(bw, out var b) ? b : 0,
                    w, h, a.GetValueOrDefault("CODECS", ""), a.GetValueOrDefault("AUDIO"), line));
            }
            else if (line.StartsWith("#EXT-X-MEDIA:"))
            {
                var a = Attributes(line);
                if (a.GetValueOrDefault("TYPE") == "AUDIO" && a.TryGetValue("URI", out var uri))
                    audio.Add(new HlsAudio(uri, a.GetValueOrDefault("GROUP-ID", ""), a.GetValueOrDefault("LANGUAGE"),
                        a.GetValueOrDefault("DEFAULT") == "YES", line));
            }
        }
        return (variants, audio);
    }

    /// <summary>
    /// Picks the variant to play: H.264 preferred (decoded everywhere), highest resolution up to
    /// <paramref name="maxHeight"/>, then highest bandwidth. Falls back to any codec.
    /// </summary>
    public static HlsVariant? ChooseVariant(IReadOnlyList<HlsVariant> variants, int maxHeight = 1080)
    {
        static bool IsH264(HlsVariant v) => v.Codecs.Contains("avc1") || v.Codecs.Length == 0;
        return variants.Where(v => v.Height <= maxHeight || v.Height == 0)
                   .OrderByDescending(IsH264).ThenByDescending(v => v.Height).ThenByDescending(v => v.Bandwidth)
                   .FirstOrDefault()
               ?? variants.OrderBy(v => v.Height).FirstOrDefault();
    }

    /// <summary>Audio rendition for the variant's group: preferred language, then DEFAULT=YES, then first.</summary>
    public static HlsAudio? ChooseAudio(IReadOnlyList<HlsAudio> audio, string? group, string? preferredLanguage)
    {
        var candidates = audio.Where(a => group is null || a.GroupId == group).ToList();
        if (candidates.Count == 0) return null;
        return candidates.FirstOrDefault(a => preferredLanguage is not null && a.Language is not null &&
                                              a.Language.StartsWith(preferredLanguage, StringComparison.OrdinalIgnoreCase))
               ?? candidates.FirstOrDefault(a => a.IsDefault)
               ?? candidates[0];
    }

    /// <summary>
    /// Builds a master playlist with only the chosen variant (and audio), with URIs rewritten from
    /// <paramref name="remotePrefix"/> (e.g. "mlhls://localhost") to <paramref name="localPrefix"/>
    /// (e.g. "http://127.0.0.1:7000"), so the local player fetches the playlists from us.
    /// </summary>
    public static string BuildMaster(HlsVariant variant, HlsAudio? audio, string remotePrefix, string localPrefix)
    {
        string Local(string uri) => uri.StartsWith(remotePrefix) ? localPrefix + uri[remotePrefix.Length..] : uri;
        var sb = new StringBuilder("#EXTM3U\n");
        if (audio is not null) sb.Append(audio.Line.Replace(audio.Uri, Local(audio.Uri))).Append('\n');
        sb.Append(variant.InfLine).Append('\n').Append(Local(variant.Uri)).Append('\n');
        return sb.ToString();
    }

    /// <summary>
    /// Master playlist with only the chosen variant (and audio), every URI made absolute against
    /// the address the original master came from: for http(s) masters (Safari, Twitch).
    /// </summary>
    public static string BuildMasterAbsolute(HlsVariant variant, HlsAudio? audio, Uri masterUri)
    {
        string Absolute(string uri) => new Uri(masterUri, uri).AbsoluteUri;
        var sb = new StringBuilder("#EXTM3U\n");
        if (audio is not null) sb.Append(audio.Line.Replace(audio.Uri, Absolute(audio.Uri))).Append('\n');
        sb.Append(variant.InfLine).Append('\n').Append(Absolute(variant.Uri)).Append('\n');
        return sb.ToString();
    }

    /// <summary>Total duration (#EXTINF sum), segment count and whether the playlist is complete (VOD).</summary>
    public static (double Duration, int Segments, bool EndList) Analyze(string mediaPlaylist)
    {
        double duration = 0;
        var segments = 0;
        foreach (var raw in mediaPlaylist.Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith("#EXTINF:")) continue;
            var value = line[8..].Split(',')[0];
            if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) duration += d;
            segments++;
        }
        return (duration, segments, mediaPlaylist.Contains("#EXT-X-ENDLIST"));
    }

    /// <summary>
    /// Expands YouTube's "#YT-EXT-CONDENSED-URL" media playlists into standard HLS.
    /// Header: BASE-URI="https://…/videoplayback", PARAMS="id,itag,…", PREFIX="s/".
    /// A segment line "s/v1/v2/…/vn" becomes BASE-URI/param1/v1/param2/v2/…/paramN/vn.
    /// Playlists without the header are returned unchanged.
    /// </summary>
    public static string ExpandYouTubeCondensed(string mediaPlaylist)
    {
        var lines = mediaPlaylist.Replace("\r", "").Split('\n');
        var header = lines.FirstOrDefault(l => l.StartsWith("#YT-EXT-CONDENSED-URL"));
        if (header is null) return mediaPlaylist;

        var a = Attributes(header);
        if (!a.TryGetValue("BASE-URI", out var baseUri) || !a.TryGetValue("PARAMS", out var paramList) ||
            !a.TryGetValue("PREFIX", out var prefix) || baseUri.Length == 0 || paramList.Length == 0 || prefix.Length == 0)
            throw new FormatException("invalid #YT-EXT-CONDENSED-URL header");
        var names = paramList.Split(',');

        var sb = new StringBuilder();
        foreach (var line in lines)
        {
            if (line.StartsWith(prefix))
            {
                var values = line[prefix.Length..].Split('/', names.Length);
                if (values.Length != names.Length) throw new FormatException($"segment has {values.Length} values, expected {names.Length}");
                sb.Append(baseUri);
                for (var i = 0; i < names.Length; i++) sb.Append('/').Append(names[i]).Append('/').Append(values[i]);
                sb.Append('\n');
            }
            else
            {
                sb.Append(line).Append('\n');
            }
        }
        return sb.ToString().TrimEnd('\n') + "\n";
    }
}

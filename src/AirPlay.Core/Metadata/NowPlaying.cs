using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace AirPlay.Core.Metadata;

/// <summary>
/// What is playing in an audio-only AirPlay session (music, or a video app sending sound only).
/// The iPhone sends the pieces separately via SET_PARAMETER; this collects them.
/// </summary>
public sealed record NowPlaying
{
    public string? Title { get; init; }
    public string? Artist { get; init; }
    public string? Album { get; init; }
    /// <summary>JPEG or PNG album art.</summary>
    public byte[]? Artwork { get; init; }
    public TimeSpan Position { get; init; }
    public TimeSpan Duration { get; init; }
    /// <summary>Stopwatch timestamp when Position was reported (to advance it while playing).</summary>
    public long PositionAtTicks { get; init; }

    public TimeSpan CurrentPosition(bool playing)
    {
        if (!playing || PositionAtTicks == 0) return Position;
        var now = Position + Stopwatch.GetElapsedTime(PositionAtTicks);
        return Duration > TimeSpan.Zero && now > Duration ? Duration : now;
    }
}

/// <summary>Parsers for the SET_PARAMETER payloads.</summary>
public static class NowPlayingParser
{
    /// <summary>
    /// DMAP ("Digital Media Access Protocol") items: 4-char tag | length (BE32) | value.
    /// The payload is an "mlit" container holding minm (title), asar (artist), asal (album), …
    /// </summary>
    public static Dictionary<string, string> ParseDmap(ReadOnlySpan<byte> data)
    {
        var result = new Dictionary<string, string>();
        Walk(data, result);
        return result;
    }

    private static void Walk(ReadOnlySpan<byte> data, Dictionary<string, string> result)
    {
        var pos = 0;
        while (pos + 8 <= data.Length)
        {
            var tag = Encoding.ASCII.GetString(data.Slice(pos, 4));
            var length = BinaryPrimitives.ReadInt32BigEndian(data.Slice(pos + 4));
            if (length < 0 || pos + 8 + length > data.Length) return;
            var value = data.Slice(pos + 8, length);
            if (tag == "mlit") Walk(value, result); // container
            else if (tag is "minm" or "asar" or "asal" or "asgn" or "ascp")
                result[tag] = Encoding.UTF8.GetString(value).Trim();
            pos += 8 + length;
        }
    }

    /// <summary>"progress: start/current/end" in RTP time (44.1 kHz) → (position, duration).</summary>
    public static (TimeSpan Position, TimeSpan Duration)? ParseProgress(string line)
    {
        if (!line.StartsWith("progress:")) return null;
        var parts = line[9..].Trim().Split('/');
        if (parts.Length != 3 ||
            !uint.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var start) ||
            !uint.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var current) ||
            !uint.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var end))
            return null;
        // RTP timestamps wrap at 2^32: unsigned subtraction handles it.
        var position = TimeSpan.FromSeconds((current - start) / 44100.0);
        var duration = TimeSpan.FromSeconds((end - start) / 44100.0);
        return (position, duration);
    }
}

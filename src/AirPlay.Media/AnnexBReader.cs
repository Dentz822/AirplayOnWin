namespace AirPlay.Media;

/// <summary>
/// Splits a raw H.264 Annex-B file (like recordings\mirror-*.h264) back into access units
/// (one picture each), so a recording can be replayed through the live decoding path.
/// Handles both 4-byte (00 00 00 01) and 3-byte (00 00 01) start codes.
/// </summary>
public static class AnnexBReader
{
    public static IEnumerable<ArraySegment<byte>> AccessUnits(byte[] data)
    {
        var auStart = -1;
        var previousType = -1;
        for (var i = 0; i + 3 < data.Length; i++)
        {
            if (data[i] != 0 || data[i + 1] != 0 || data[i + 2] != 1) continue;
            var codeStart = i > 0 && data[i - 1] == 0 ? i - 1 : i;
            var type = data[i + 3] & 0x1F;
            // A picture starts at SPS (7), or at a slice (1/5) not directly preceded by SPS/PPS/SEI.
            var startsPicture = type == 7 || (type is 1 or 5 && previousType is not (7 or 8 or 6));
            if (startsPicture)
            {
                if (auStart >= 0) yield return new ArraySegment<byte>(data, auStart, codeStart - auStart);
                auStart = codeStart;
            }
            previousType = type;
            i += 2;
        }
        if (auStart >= 0) yield return new ArraySegment<byte>(data, auStart, data.Length - auStart);
    }
}

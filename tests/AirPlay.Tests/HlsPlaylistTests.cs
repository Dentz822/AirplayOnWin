using AirPlay.Core.Video;

namespace AirPlay.Tests;

public class HlsPlaylistTests
{
    // Shape of a YouTube master playlist as relayed by the iPhone (shortened).
    private const string Master = """
        #EXTM3U
        #EXT-X-INDEPENDENT-SEGMENTS
        #EXT-X-MEDIA:URI="mlhls://localhost/itag/233/mediadata.m3u8",TYPE=AUDIO,GROUP-ID="233",LANGUAGE="en",NAME="English",DEFAULT=NO,AUTOSELECT=YES
        #EXT-X-MEDIA:URI="mlhls://localhost/itag/234/mediadata.m3u8",TYPE=AUDIO,GROUP-ID="234",LANGUAGE="en",NAME="English",DEFAULT=YES,AUTOSELECT=YES
        #EXT-X-STREAM-INF:BANDWIDTH=1500000,CODECS="avc1.4D401F,mp4a.40.2",RESOLUTION=1280x720,FRAME-RATE=30,AUDIO="234"
        mlhls://localhost/itag/136/mediadata.m3u8
        #EXT-X-STREAM-INF:BANDWIDTH=4000000,CODECS="avc1.640028,mp4a.40.2",RESOLUTION=1920x1080,FRAME-RATE=30,AUDIO="234"
        mlhls://localhost/itag/137/mediadata.m3u8
        #EXT-X-STREAM-INF:BANDWIDTH=9000000,CODECS="vp09.00.51.08,mp4a.40.2",RESOLUTION=3840x2160,FRAME-RATE=30,AUDIO="234"
        mlhls://localhost/itag/313/mediadata.m3u8
        #EXT-X-STREAM-INF:BANDWIDTH=2500000,CODECS="vp09.00.40.08,mp4a.40.2",RESOLUTION=1920x1080,FRAME-RATE=30,AUDIO="233"
        mlhls://localhost/itag/248/mediadata.m3u8
        """;

    [Fact]
    public void ParsesVariantsAndAudio()
    {
        var (variants, audio) = HlsPlaylist.ParseMaster(Master);
        Assert.Equal(4, variants.Count);
        Assert.Equal(2, audio.Count);
        Assert.Equal((1920, 1080), (variants[1].Width, variants[1].Height));
        Assert.Equal("234", variants[1].AudioGroup);
        Assert.Equal("mlhls://localhost/itag/137/mediadata.m3u8", variants[1].Uri);
        Assert.True(audio[1].IsDefault);
    }

    [Fact]
    public void ChoosesBestH264UpTo1080p()
    {
        var (variants, audio) = HlsPlaylist.ParseMaster(Master);
        var v = HlsPlaylist.ChooseVariant(variants)!;
        Assert.Equal("mlhls://localhost/itag/137/mediadata.m3u8", v.Uri); // not 4K, not VP9
        Assert.Equal("mlhls://localhost/itag/136/mediadata.m3u8", HlsPlaylist.ChooseVariant(variants, 720)!.Uri);
        var a = HlsPlaylist.ChooseAudio(audio, v.AudioGroup, "zh")!;
        Assert.Equal("mlhls://localhost/itag/234/mediadata.m3u8", a.Uri);
    }

    [Fact]
    public void PrefersRequestedAudioLanguage()
    {
        var audio = new List<HlsAudio>
        {
            new("a/en", "g", "en", true, ""), new("a/zh", "g", "zh-TW", false, ""),
        };
        Assert.Equal("a/zh", HlsPlaylist.ChooseAudio(audio, "g", "zh")!.Uri);
        Assert.Equal("a/en", HlsPlaylist.ChooseAudio(audio, "g", "fr")!.Uri);
    }

    [Fact]
    public void BuildsLocalMasterWithOnlyTheChosenStreams()
    {
        var (variants, audio) = HlsPlaylist.ParseMaster(Master);
        var v = HlsPlaylist.ChooseVariant(variants)!;
        var a = HlsPlaylist.ChooseAudio(audio, v.AudioGroup, null)!;
        var local = HlsPlaylist.BuildMaster(v, a, "mlhls://localhost", "http://127.0.0.1:7000");

        Assert.StartsWith("#EXTM3U\n", local);
        Assert.Contains("URI=\"http://127.0.0.1:7000/itag/234/mediadata.m3u8\"", local);
        Assert.Contains("\nhttp://127.0.0.1:7000/itag/137/mediadata.m3u8\n", local);
        Assert.DoesNotContain("itag/313", local);
        Assert.DoesNotContain("mlhls://", local);
    }

    [Fact]
    public void ExpandsYouTubeCondensedPlaylist()
    {
        const string condensed = """
            #EXTM3U
            #EXT-X-VERSION:7
            #EXT-X-TARGETDURATION:5
            #YT-EXT-CONDENSED-URL:BASE-URI="https://rr1.googlevideo.com/videoplayback",PARAMS="id,itag,sq",PREFIX="s/"
            #EXTINF:5.005,
            s/abc123/137/0
            #EXTINF:5.005,
            s/abc123/137/1
            #EXT-X-ENDLIST
            """;
        var expanded = HlsPlaylist.ExpandYouTubeCondensed(condensed);
        Assert.Contains("\nhttps://rr1.googlevideo.com/videoplayback/id/abc123/itag/137/sq/0\n", expanded);
        Assert.Contains("\nhttps://rr1.googlevideo.com/videoplayback/id/abc123/itag/137/sq/1\n", expanded);
        Assert.DoesNotContain("\ns/", expanded);

        var (duration, segments, endList) = HlsPlaylist.Analyze(expanded);
        Assert.Equal(10.01, duration, 2);
        Assert.Equal(2, segments);
        Assert.True(endList);
    }

    [Fact]
    public void BuildsAbsoluteMasterForTwitchStylePlaylists()
    {
        const string twitch = "#EXTM3U\n" +
            "#EXT-X-STREAM-INF:BANDWIDTH=160000,CODECS=\"mp4a.40.2\",VIDEO=\"audio_only\"\n" +
            "https://cdn.example/audio_only/index.m3u8\n" +
            "#EXT-X-STREAM-INF:BANDWIDTH=6000000,RESOLUTION=1920x1080,CODECS=\"avc1.64002A,mp4a.40.2\",VIDEO=\"chunked\"\n" +
            "https://cdn.example/chunked/index.m3u8\n" +
            "#EXT-X-STREAM-INF:BANDWIDTH=3000000,RESOLUTION=1280x720,CODECS=\"avc1.4D401F,mp4a.40.2\",VIDEO=\"720p60\"\n" +
            "720p60/index.m3u8\n";
        var (variants, audio) = HlsPlaylist.ParseMaster(twitch);
        var v = HlsPlaylist.ChooseVariant(variants)!;
        Assert.Equal("https://cdn.example/chunked/index.m3u8", v.Uri); // not audio-only

        var v720 = HlsPlaylist.ChooseVariant(variants, 720)!;
        var master = HlsPlaylist.BuildMasterAbsolute(v720, null, new Uri("https://usher.example/api/channel/hls/x.m3u8?token=1"));
        Assert.Contains("\nhttps://usher.example/api/channel/hls/720p60/index.m3u8\n", master); // relative made absolute
        Assert.DoesNotContain("audio_only", master);
    }

    [Fact]
    public void LeavesNormalPlaylistsAlone()
    {
        const string normal = "#EXTM3U\n#EXTINF:4,\nhttps://cdn/x.ts\n";
        Assert.Equal(normal, HlsPlaylist.ExpandYouTubeCondensed(normal));
    }
}

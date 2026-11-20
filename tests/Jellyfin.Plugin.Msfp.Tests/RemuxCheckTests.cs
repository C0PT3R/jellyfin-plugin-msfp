using Jellyfin.Plugin.Msfp.Playback;
using Xunit;

namespace Jellyfin.Plugin.Msfp.Tests;

public class RemuxCheckTests
{
    [Theory]
    [InlineData("-i \"x.mkv\" -map 0:0 -map 0:1 -codec:v:0 copy -bsf:v h264_mp4toannexb -codec:a:0 aac", "copy")]
    [InlineData("-i \"x.mkv\" -codec:v:0 libx264 -preset veryfast -codec:a:0 copy", "libx264")]
    [InlineData("-i \"x.mkv\" -codec:v:0 h264_qsv -low_power 1", "h264_qsv")]
    [InlineData("-i \"x.mkv\" -vn -codec:a:0 aac", null)]
    public void TheVideoEncoderIsReadFromJellyfinsCommand(string arguments, string? codec) =>
        Assert.Equal(codec, JellyfinPlayback.VideoCodecOf(arguments));
}

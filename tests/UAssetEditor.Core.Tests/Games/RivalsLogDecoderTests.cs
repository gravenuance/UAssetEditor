using System.Text;
using UAssetEditor.Core.Games.Rivals;
using static UAssetEditor.Core.Tests.Games.RivalsLogTestData;

namespace UAssetEditor.Core.Tests.Games;

public class RivalsLogDecoderTests
{
    private static (string Text, RivalsLogDecodeResult Result) Decode(byte[] log)
    {
        using var output = new MemoryStream();
        var result = RivalsLogDecoder.Decode(log, output);
        return (Encoding.UTF8.GetString(output.ToArray()), result);
    }

    [Fact]
    public void DecodesOpensslCiphertext_AndRestartsTheCounterPerRecord()
    {
        var (text, result) = Decode(Log(Frame(TwoLinesCipher), Frame(SecondCipher)));

        Assert.Equal(TwoLinesText + SecondText, text);
        Assert.Equal(new RivalsLogDecodeResult(2, 0, null, false), result);
    }

    [Fact]
    public void InflatesAZlibRecord()
    {
        var (text, result) = Decode(Log(Frame(CompressedCipher)));

        Assert.Equal(CompressedText, text);
        Assert.Equal(new RivalsLogDecodeResult(1, 0, null, false), result);
    }

    [Fact]
    public void ARecordThatFailsToInflate_IsCountedNotSwallowed_AndLaterRecordsStillDecode()
    {
        var (text, result) = Decode(Log(Frame(CorruptZlibCipher), Frame(SecondCipher)));

        Assert.Equal(SecondText, text);
        Assert.Equal(2, result.Records);
        Assert.Equal(1, result.InflateFailures);
        Assert.Null(result.StoppedAtOffset);
    }

    [Fact]
    public void ATruncatedFrame_StopsAndReportsItsOffset()
    {
        var first = Frame(TwoLinesCipher);
        byte[] truncated = [0x64, 0x00, 0x00, 0x00, 0xAA, 0xBB, 0xCC];

        var (text, result) = Decode(Log(first, truncated));

        Assert.Equal(TwoLinesText, text);
        Assert.Equal(1, result.Records);
        Assert.Equal(Magic.Length + first.Length, result.StoppedAtOffset);
        Assert.False(result.HasTrailer);
    }

    [Fact]
    public void AFrameWhoseClosingSizeDisagrees_StopsAtItsOffset()
    {
        var first = Frame(SecondCipher);
        var broken = Frame(TwoLinesCipher);
        broken[^1] ^= 0xFF;

        var (text, result) = Decode(Log(first, broken));

        Assert.Equal(SecondText, text);
        Assert.Equal(Magic.Length + first.Length, result.StoppedAtOffset);
    }

    [Fact]
    public void AHugeSizeWord_StopsInsteadOfOverflowingTheBoundsCheck()
    {
        byte[] huge = [0xFF, 0xFF, 0xFF, 0x7F, 0, 0, 0, 0, 0, 0, 0, 0];

        var (text, result) = Decode(Log(huge));

        Assert.Equal("", text);
        Assert.Equal(Magic.Length, result.StoppedAtOffset);
    }

    [Fact]
    public void BytesTooFewForASizeWord_AreReportedAsTheStopOffset()
    {
        var first = Frame(SecondCipher);

        var (_, result) = Decode(Log(first, [1, 2]));

        Assert.Equal(1, result.Records);
        Assert.Equal(Magic.Length + first.Length, result.StoppedAtOffset);
    }

    [Fact]
    public void ThePlaintextShmTrailer_IsAppendedUnderAHeading()
    {
        var (text, result) = Decode(Log(Frame(SecondCipher), Encoding.ASCII.GetBytes("<SHM_LOG_BEGIN>tail line\n")));

        Assert.Equal(SecondText + "\n----- SHM trailer -----\ntail line\n", text);
        Assert.Equal(new RivalsLogDecodeResult(1, 0, null, true), result);
    }

    [Fact]
    public void AShmTrailerAfterAnUnreadableFrame_IsStillFound()
    {
        // The marker sits 30 bytes in, beyond the look-ahead window of the normal check, so only the fallback search that runs on a bad frame can find it.
        var garbage = Enumerable.Repeat((byte)0x7F, 30).ToArray();

        var (text, result) = Decode(Log(Frame(SecondCipher), garbage, Encoding.ASCII.GetBytes("<SHM_LOG_BEGIN>tail")));

        Assert.EndsWith("\n----- SHM trailer -----\ntail", text, StringComparison.Ordinal);
        Assert.True(result.HasTrailer);
        Assert.Null(result.StoppedAtOffset);
    }

    [Fact]
    public void TheTestEncryptHelper_MatchesTheOpensslFixture()
    {
        Assert.Equal(TwoLinesCipher, Convert.ToHexString(Encrypt(Encoding.ASCII.GetBytes(TwoLinesText))));
    }

    [Fact]
    public void AZlibBombRecord_StopsInflatingAtTheLimit_AndSaysSo()
    {
        // 1 MiB of 'a' packs into about a kilobyte; the limit lets 1000 bytes of it out.
        var bomb = Frame(Encrypt(Zlib(Enumerable.Repeat((byte)'a', 1 << 20).ToArray())));
        using var output = new MemoryStream();

        var result = RivalsLogDecoder.Decode(Log(bomb, Frame(SecondCipher)), output, maxDecodedBytes: 1000);

        Assert.Equal(new string('a', 1000), Encoding.ASCII.GetString(output.ToArray()));
        Assert.Equal(new RivalsLogDecodeResult(1, 0, null, false, CutAtLimit: 1000), result);
        Assert.Contains("stopped after 1000 bytes of text, the most one log is decoded to; the rest of the records was not decoded, so a crash near the end may be missing.", result.Problems());
    }

    [Fact]
    public void TheShmTrailer_IsStillWrittenAfterTheRecordsAreCut()
    {
        using var output = new MemoryStream();

        var result = RivalsLogDecoder.Decode(
            Log(Frame(TwoLinesCipher), Frame(SecondCipher), Encoding.ASCII.GetBytes("<SHM_LOG_BEGIN>crash here\n")), output, maxDecodedBytes: 8);

        Assert.Equal("line one\n----- SHM trailer -----\ncrash here\n", Encoding.UTF8.GetString(output.ToArray()));
        Assert.Equal(new RivalsLogDecodeResult(1, 0, null, true, CutAtLimit: 8), result);
    }

    [Fact]
    public void PlainRecordsPastTheLimit_AreCutAtTheLimit()
    {
        using var output = new MemoryStream();

        var result = RivalsLogDecoder.Decode(Log(Frame(SecondCipher), Frame(TwoLinesCipher)), output, maxDecodedBytes: 30);

        // SecondText is 21 bytes, so 9 of the next record fit.
        Assert.Equal(SecondText + "line one ", Encoding.UTF8.GetString(output.ToArray()));
        Assert.Equal(2, result.Records);
        Assert.Equal(30, result.CutAtLimit);
    }

    [Fact]
    public void OutputExactlyAtTheLimit_IsNotReportedAsCut()
    {
        using var output = new MemoryStream();

        var result = RivalsLogDecoder.Decode(Log(Frame(SecondCipher)), output, maxDecodedBytes: SecondText.Length);

        Assert.Equal(SecondText, Encoding.UTF8.GetString(output.ToArray()));
        Assert.Null(result.CutAtLimit);
        Assert.Empty(result.Problems());
    }

    [Fact]
    public void ProblemsName_InflateFailures_AndTheStopOffset()
    {
        var result = new RivalsLogDecodeResult(3, 2, 0x1f, false);

        Assert.Equal(
            ["2 record(s) failed to inflate; their text is cut short or missing.",
             "stopped at 0x1f, which is not a whole record (truncated or damaged log); the rest was not decoded."],
            result.Problems());
    }

    [Fact]
    public void AFileWithoutTheMagic_IsRejected()
    {
        Assert.Throws<InvalidDataException>(() => Decode(Encoding.ASCII.GetBytes("plain text, not a log")));
    }

    [Fact]
    public void TheConsumerCanStopTheDecodeEarly()
    {
        var chunks = new List<string>();

        var result = RivalsLogDecoder.Decode(Log(Frame(TwoLinesCipher), Frame(SecondCipher)), chunk =>
        {
            chunks.Add(Encoding.UTF8.GetString(chunk.Span));
            return false;
        });

        Assert.Equal([TwoLinesText], chunks);
        Assert.Equal(1, result.Records);
    }

    [Theory]
    [InlineData("00000000000000000000000000000000", "00000000000000000000000000000001")]
    [InlineData("000000000000000000000000000000FF", "00000000000000000000000000000100")]
    [InlineData("0000000000000000000000000000FFFF", "00000000000000000000000000010000")]
    [InlineData("0102030405060708090A0B0C0D0E0FFF", "0102030405060708090A0B0C0D0E1000")]
    [InlineData("FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF", "00000000000000000000000000000000")]
    public void IncrementCounter_CarriesBigEndianAndWrapsAtAllOnes(string before, string after)
    {
        var counter = Convert.FromHexString(before);

        RivalsLogDecoder.IncrementCounter(counter);

        Assert.Equal(after, Convert.ToHexString(counter));
    }
}

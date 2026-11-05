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

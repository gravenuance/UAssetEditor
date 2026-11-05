using System.Buffers.Binary;
using System.Text;

namespace UAssetEditor.Core.Tests.Games;

/// <summary>
/// Log bytes for the decoder tests. Every ciphertext was produced independently with
/// <c>openssl enc -aes-128-ctr -K &lt;hex of key&gt; -iv &lt;hex of IV&gt; -nopad</c>
/// (key "ezFAgrXnhBOZgFFD", IV "_NMxNi0DK7_qHC_i"); zlib data came from Python's zlib.
/// </summary>
internal static class RivalsLogTestData
{
    public const string Magic = "logversion000";

    /// <summary>The plaintext of <see cref="TwoLinesCipher"/>: 40 bytes, so three counter blocks.</summary>
    public const string TwoLinesText = "line one of the log\nline two of the log\n";

    public const string TwoLinesCipher = "702B3E38D85D423C04DCF7DC1D8538A2DCAB5EE6041FFB15C6D947DA311B5381D958EE251F21DFB9";

    /// <summary>A second record: its counter restarts at the IV, it does not continue from the first record.</summary>
    public const string SecondText = "second record, plain\n";

    public const string SecondCipher = "6F27333296560C2B41D0FE8E0DC17DF2DCA5508262";

    /// <summary>zlib of "compressed record text\n" three times, encrypted.</summary>
    public const string CompressedText = "compressed record text\ncompressed record text\ncompressed record text\n";

    public const string CompressedCipher = "64DE1B93371F0413099DDFB138C517CF7EEB73BD403F385807E716FC09749208B77A";

    /// <summary>78 9C FF FF FF FF FF FF: a zlib header followed by garbage, encrypted.</summary>
    public const string CorruptZlibCipher = "64DEAFA207CDD3A6";

    /// <summary>"LogInit: Display: Engine Version: 5.3.2-4008108+++depot_marvel+S10.5_release\n", encrypted.</summary>
    public const string EngineLine4008108Cipher =
        "502D3714965B586304F7F88F19813CFB8AE47C820F1FFB15C6FB55C7621D5ACF9710BE2B40608A9ECD9C0B6826ECD8058D9DCD9A45D2C64B001AD0E5D30245EDA708AFAF5845443C3C7ADCBC21";

    /// <summary>The same line for build 3939753.</summary>
    public const string EngineLine3939753Cipher =
        "502D3714965B586304F7F88F19813CFB8AE47C820F1FFB15C6FB55C7621D5ACF9710BE2B40608A9ECA95086920E9D3058D9DCD9A45D2C64B001AD0E5D30245EDA708AFAF5845443C3C7ADCBC21";

    /// <summary>i32 size, payload, i32 size.</summary>
    public static byte[] Frame(string cipherHex) => Frame(Convert.FromHexString(cipherHex));

    public static byte[] Frame(byte[] payload)
    {
        var frame = new byte[payload.Length + 8];
        BinaryPrimitives.WriteInt32LittleEndian(frame, payload.Length);
        payload.CopyTo(frame, 4);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(4 + payload.Length), payload.Length);
        return frame;
    }

    public static byte[] Log(params byte[][] parts) => [.. Encoding.ASCII.GetBytes(Magic), .. parts.SelectMany(part => part)];
}

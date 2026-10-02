using System.Text;
using Bytex.Live.Network;

namespace Bytex.Live.Tests.Network;

// Why: a wrong signature means every private request is rejected. Expected digests come from published vectors:
// the Binance API documentation example and RFC 4231 test case 2, never from the code under test.
public sealed class HmacSignerTests
{
    // Binance "SIGNED endpoint examples" key pair; published in the public API docs, not a live credential.
    internal const string BinanceDocSecret = "NhqPtmdSJYdKjVHjA7PZj4Mge3R5YNiP1e3UZjInClVN65XAbvqqM6A7H5fATj0j";
    internal const string BinanceDocQuery = "symbol=LTCBTC&side=BUY&type=LIMIT&timeInForce=GTC&quantity=1&price=0.1&recvWindow=5000&timestamp=1499827319559";
    internal const string BinanceDocSignature = "c8db56825ae71d6d79447849e617115f4a920fa2acdcab2b053c4b2838bd6b71";

    internal const string RfcKey = "Jefe";
    internal const string RfcData = "what do ya want for nothing?";
    internal const string RfcSha256 = "5bdcc146bf60754e6a042426089575c75a003f089d2739839dec58b964ec3843";
    internal const string RfcSha512 = "164b7a7bfcf819e2e395fbe73b56e0a387bd64222e831fd610270cd7ea2505549758bf75c05a994a6d034f65f8f0e6fdcaeab1a34d4a6b4b636e070a38bce737";

    [Fact]
    public void Sha256Hex_reproduces_the_signature_from_the_binance_documentation()
    {
        Assert.Equal(BinanceDocSignature, HmacSigner.Sha256Hex(BinanceDocSecret, BinanceDocQuery));
    }

    [Fact]
    public void Sha256Hex_matches_rfc_4231_test_case_2_in_lower_case_hex()
    {
        Assert.Equal(RfcSha256, HmacSigner.Sha256Hex(RfcKey, RfcData));
    }

    [Fact]
    public void Sha512Hex_matches_rfc_4231_test_case_2_in_lower_case_hex()
    {
        Assert.Equal(RfcSha512, HmacSigner.Sha512Hex(RfcKey, RfcData));
    }

    [Fact]
    public void Base64_variants_encode_the_same_rfc_4231_digests()
    {
        string expected256 = Convert.ToBase64String(Convert.FromHexString(RfcSha256));
        string expected512 = Convert.ToBase64String(Convert.FromHexString(RfcSha512));

        Assert.Equal(expected256, HmacSigner.Sha256Base64(RfcKey, RfcData));
        Assert.Equal(expected512, HmacSigner.Sha512Base64(Encoding.ASCII.GetBytes(RfcKey), Encoding.ASCII.GetBytes(RfcData)));
    }

    [Fact]
    public void Secret_and_payload_are_hashed_as_utf8()
    {
        // Reference digest from the framework primitive over explicit UTF-8 bytes; this pins the encoding, not the HMAC maths.
        byte[] key = Encoding.UTF8.GetBytes("ключ");
        byte[] data = Encoding.UTF8.GetBytes("данные");
        string expected = Convert.ToHexStringLower(System.Security.Cryptography.HMACSHA256.HashData(key, data));

        Assert.Equal(expected, HmacSigner.Sha256Hex("ключ", "данные"));
    }
}

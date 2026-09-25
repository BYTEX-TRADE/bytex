using System.Text;
using Bytex.Adapters.Hyperliquid;
using Bytex.Adapters.Tests.Fixtures;
using Bytex.Core.Model.Identifiers;

namespace Bytex.Adapters.Tests.Hyperliquid;

// Why: this venue's authentication is not a key the exchange issued and a signature over a request string. It is a
// wallet signature - EIP-712 typed data over a MessagePack encoding of the action, hashed with Keccak-256 and signed
// with a secp256k1 key - and every part of that had to be written out, because the framework has none of it. SHA3-256
// is not Keccak-256, ECDsa will not produce a recovery id, and nothing anywhere will msgpack an object.
//
// So the risk is not that it fails. The risk is that it SUCCEEDS at producing 65 plausible bytes that the venue
// disagrees with, because a signature it disagrees with recovers some other perfectly valid address and the order is
// refused as belonging to an account nobody has heard of. There is no error to read.
//
// Two things make that testable. The published vectors below pin Keccak-256 and the key derivation independently of
// this venue. And the venue itself NAMES THE ADDRESS IT RECOVERED in a refusal - so signing an action with a key
// nobody has funded and reading the address back is a complete check of the whole pipeline, field order and all, with
// no account and no money. That was done against the live venue on 2026-09-25 for every action this adapter sends,
// on mainnet and on the test network; the connection ids and digests recorded here are what it answered to.
public sealed class HyperliquidSigningTests
{
    /// <summary>
    /// A key nobody has used, and the address it controls. The live venue was asked to recover a signer from an
    /// action signed with this key and answered with exactly this address, which is what every recorded digest
    /// below is evidence of.
    /// </summary>
    private const string ProbeKey = "0x1111111111111111111111111111111111111111111111111111111111111112";

    private const string ProbeAddress = "0xc06d73162e9bffbcfbf1da59c511002a8f9155e5";

    private static HyperliquidCredentials Probe() =>
        HyperliquidVenue.Credentials(new HyperliquidExecutionClientConfig { PrivateKey = ProbeKey });

    [Theory]

    // The published Keccak-256 digests. These are the vectors that caught the bug this code had: the field prime was
    // written as one 64-character literal with one F missing, BigInteger.Parse took the 63-digit number without a
    // word, and every point came out reduced modulo the wrong field. Nothing threw and the addresses were 20
    // plausible bytes.
    [InlineData("", "c5d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a470")]
    [InlineData("abc", "4e03657aea45a94fc7d47ba826c8d667c0d1e6e33a64a036ec44f58fa12d6c45")]
    public void Keccak_matches_the_published_vectors(string text, string expected)
    {
        // Reached through the one public thing built on it, because an address IS a Keccak digest of a public key
        // and a digest that is wrong cannot produce a right address.
        Assert.Equal(expected, Convert.ToHexStringLower(Digest(text)));
    }

    [Theory]

    // The canonical Ethereum test keys and the addresses they control, which pin the curve, the scalar
    // multiplication and the last-twenty-bytes-of-the-hash rule all at once.
    [InlineData("0x0000000000000000000000000000000000000000000000000000000000000001", "0x7e5f4552091a69125d5dfcb7b8c2659029395bdf")]
    [InlineData("0x4646464646464646464646464646464646464646464646464646464646464646", "0x9d8a62f656a8d1615c1294fd71e9cfb3e4855a4f")]
    public void A_private_key_derives_the_address_the_published_vectors_give(string key, string address)
    {
        HyperliquidCredentials credentials = HyperliquidVenue.Credentials(new HyperliquidExecutionClientConfig { PrivateKey = key });

        Assert.Equal(address, credentials.Signer);

        // And with no account configured, the account IS the signer - which is right for a wallet key and is the
        // default an API wallet has to override.
        Assert.Equal(address, credentials.Account);
    }

    [Fact]
    public void A_key_that_is_not_a_key_is_refused_where_a_person_can_see_why()
    {
        // The commonest mistake on this venue is pasting an address where a key goes, so the refusal has to say
        // what the length should have been rather than fail somewhere inside the arithmetic.
        ArgumentException tooShort = Assert.Throws<ArgumentException>(
            () => HyperliquidVenue.Credentials(new HyperliquidExecutionClientConfig { PrivateKey = "0xdeadbeef" }));
        Assert.Contains("32 bytes", tooShort.Message, StringComparison.Ordinal);

        Assert.Throws<ArgumentException>(
            () => HyperliquidVenue.Credentials(new HyperliquidExecutionClientConfig { PrivateKey = "0x" + new string('z', 64) }));

        // Zero is not a scalar the curve has, and neither is anything at or past its order.
        Assert.Throws<ArgumentException>(
            () => HyperliquidVenue.Credentials(new HyperliquidExecutionClientConfig { PrivateKey = "0x" + new string('0', 64) }));
    }

    [Fact]
    public void An_account_address_separates_the_signer_from_the_account_it_trades()
    {
        // The API wallet case, and the one that is invisible when it goes wrong: the signer and the account differ,
        // and every read on this venue is keyed by the ACCOUNT. Configured wrongly, everything signs, everything is
        // accepted and every read comes back empty.
        HyperliquidCredentials credentials = HyperliquidVenue.Credentials(new HyperliquidExecutionClientConfig
        {
            PrivateKey = ProbeKey,
            AccountAddress = "0x10d944a35f99c141b9121ec3693000531825bef8",
        });

        Assert.Equal(ProbeAddress, credentials.Signer);
        Assert.Equal("0x10d944a35f99c141b9121ec3693000531825bef8", credentials.Account);
        Assert.NotEqual(credentials.Signer, credentials.Account);
    }

    [Fact]
    public void The_credential_never_prints_the_key()
    {
        HyperliquidCredentials credentials = Probe();

        Assert.DoesNotContain("1111", credentials.ToString(), StringComparison.Ordinal);

        // The address IS public information - it is on a blockchain - so it is the half worth printing.
        Assert.Contains(ProbeAddress, credentials.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void An_order_packs_to_the_bytes_the_venue_hashes()
    {
        // The MessagePack encoding, pinned byte for byte. This is the one that cannot be checked by reading it: the
        // venue re-encodes the action from its own types before hashing, so the FIELD ORDER is part of the digest
        // and JSON key order is not. These bytes are what the live venue agreed with.
        HyperliquidAction action = HyperliquidActions.Order([Limit()]);

        Assert.Equal(
            "83a474797065a56f72646572a66f72646572739186a16100a162c3a170a53130303030a173a5302e303031a172c2a17481a56c696d697481a3746966a3477463a867726f7570696e67a26e61",
            Convert.ToHexStringLower(action.Packed));

        // And the JSON beside it, built from the same fields so the two cannot disagree about a value. A price and
        // a size are STRINGS: a numeric price is refused at HTTP 422 before the signature is looked at, measured.
        Assert.Equal(
            """{"type":"order","orders":[{"a":0,"b":true,"p":"10000","s":"0.001","r":false,"t":{"limit":{"tif":"Gtc"}}}],"grouping":"na"}""",
            action.Json);
    }

    [Theory]

    // Every action this adapter sends, with the bytes the live venue recovered the right signer from. A change to
    // any of these is a change the venue will disagree with, and it will disagree by refusing orders as belonging
    // to an account that does not exist.
    [InlineData("order", "83a474797065a56f72646572a66f72646572739186a16100a162c3a170a53130303030a173a5302e303031a172c2a17481a56c696d697481a3746966a3477463a867726f7570696e67a26e61")]
    [InlineData("cancel", "82a474797065a663616e63656ca763616e63656c739182a16100a16fce075bcd15")]
    [InlineData("cancelByCloid", "82a474797065ad63616e63656c4279436c6f6964a763616e63656c739182a5617373657400a5636c6f6964d92230783030303030303030303030303030303030303030303030303030303030303031")]
    [InlineData("modify", "83a474797065a66d6f64696679a36f6964ce075bcd15a56f7264657286a16100a162c3a170a53131303030a173a5302e303032a172c2a17481a56c696d697481a3746966a3477463")]
    [InlineData("updateLeverage/cross", "84a474797065ae7570646174654c65766572616765a5617373657400a7697343726f7373c3a86c6576657261676505")]
    [InlineData("updateLeverage/isolated", "84a474797065ae7570646174654c65766572616765a5617373657401a7697343726f7373c2a86c6576657261676503")]
    [InlineData("order/trigger", "83a474797065a56f72646572a66f72646572739186a16100a162c2a170a53830303030a173a5302e303031a172c3a17481a77472696767657283a869734d61726b6574c3a9747269676765725078a53830353030a47470736ca2736ca867726f7570696e67a26e61")]
    [InlineData("order/cloid", "83a474797065a56f72646572a66f72646572739187a16105a162c2a170a53132302e35a173a4312e3235a172c3a17481a56c696d697481a3746966a3416c6fa163d92230783030303030303030303030303030303030303030303030303030303030303031a867726f7570696e67a26e61")]
    [InlineData("order/asset-233", "83a474797065a56f72646572a66f72646572739186a161cce9a162c3a170a131a173a131a172c2a17481a56c696d697481a3746966a3496f63a867726f7570696e67a26e61")]
    [InlineData("cancel/large-oid", "82a474797065a663616e63656ca763616e63656c739182a161cce9a16fcf00005af3107a3fff")]
    public void Every_action_packs_to_the_bytes_the_live_venue_recovered_from(string name, string packed)
    {
        Assert.Equal(packed, Convert.ToHexStringLower(Action(name).Packed));
    }

    [Fact]
    public void An_integer_is_packed_in_the_narrowest_form_that_holds_it()
    {
        // The encoding rule that decides the digest for any asset past 127. A number written wider than it needs to
        // be is a different byte string, so asset 0 is a bare byte, asset 233 is a one-byte unsigned, and an order
        // id in the hundreds of billions is an eight-byte one. All three are in the recorded actions above; this
        // names the rule they are evidence of.
        Assert.Contains("a16100", Packed(0), StringComparison.Ordinal);
        Assert.Contains("a161cce9", Packed(233), StringComparison.Ordinal);
        Assert.Contains("a161cd0100", Packed(256), StringComparison.Ordinal);
    }

    [Fact]
    public void The_connection_id_appends_the_nonce_and_a_vault_byte()
    {
        // The first half of the digest, which the live venue agreed with. A bare zero says "no vault"; with one the
        // byte is a one and the address follows, so a vault changes the LENGTH of what is hashed.
        HyperliquidAction action = HyperliquidActions.Order([Limit()]);

        Assert.Equal(
            "e0a317e740fab4621add0ae62c72815b776a7f1b789f1755afeebf1a3ff281be",
            Convert.ToHexStringLower(HyperliquidSigner.ConnectionId(action.Packed, 1700000000000L, [])));

        // A different nonce is a different id, which is what stops a signature being replayed.
        Assert.NotEqual(
            Convert.ToHexStringLower(HyperliquidSigner.ConnectionId(action.Packed, 1700000000000L, [])),
            Convert.ToHexStringLower(HyperliquidSigner.ConnectionId(action.Packed, 1700000000001L, [])));

        // And a vault address is not the same as none, which a client that always wrote the zero byte could never
        // have discovered.
        Assert.NotEqual(
            Convert.ToHexStringLower(HyperliquidSigner.ConnectionId(action.Packed, 1700000000000L, [])),
            Convert.ToHexStringLower(HyperliquidSigner.ConnectionId(action.Packed, 1700000000000L, new byte[20])));
    }

    [Fact]
    public void The_digest_is_the_one_the_live_venue_recovered_a_signer_from()
    {
        HyperliquidAction action = HyperliquidActions.Order([Limit()]);

        Assert.Equal(
            "1f23df907fc6ae17e926857bbc74ab3d7845ed4849e6abde519afb9e94276747",
            Convert.ToHexStringLower(HyperliquidSigner.Digest(action.Packed, 1700000000000L, [], mainnet: true)));
    }

    [Fact]
    public void A_mainnet_digest_is_not_a_testnet_one()
    {
        // One letter of the typed data - "a" for mainnet, "b" for testnet - is the whole of the replay protection
        // between the two networks, and it was measured rather than taken on trust: a mainnet signature sent to the
        // test network recovered a DIFFERENT address, so the letter really is load-bearing.
        HyperliquidAction action = HyperliquidActions.Order([Limit()]);

        Assert.NotEqual(
            Convert.ToHexStringLower(HyperliquidSigner.Digest(action.Packed, 1L, [], mainnet: true)),
            Convert.ToHexStringLower(HyperliquidSigner.Digest(action.Packed, 1L, [], mainnet: false)));
    }

    [Fact]
    public void The_network_is_taken_from_the_host_rather_than_configured_separately()
    {
        // So that pointing a client at the test network cannot leave it signing mainnet-valid signatures, which
        // would make the two replayable against each other.
        Assert.True(HyperliquidVenue.IsMainnet(new HyperliquidDataClientConfig()));
        Assert.False(HyperliquidVenue.IsMainnet(new HyperliquidDataClientConfig { BaseUrlHttp = "https://api.hyperliquid-testnet.xyz" }));
        Assert.False(HyperliquidVenue.IsMainnet(new HyperliquidDataClientConfig { BaseUrlHttp = "http://127.0.0.1:1234" }));
    }

    [Fact]
    public void A_signature_over_one_action_is_the_same_bytes_every_time()
    {
        // The nonce inside the signature is RFC 6979 deterministic, so this is pinnable at all - and the recorded r
        // and s are the ones the live venue recovered the probe's own address from.
        HyperliquidAction action = HyperliquidActions.Order([Limit()]);
        HyperliquidSignature first = HyperliquidSigner.Sign(action.Packed, 1700000000000L, [], mainnet: true, Probe().PrivateKey!);
        HyperliquidSignature again = HyperliquidSigner.Sign(action.Packed, 1700000000000L, [], mainnet: true, Probe().PrivateKey!);

        Assert.Equal(first, again);
        Assert.Equal("0x3e43d07da8d27f6f115332f539ce634f5496499f77b3b301638178fa5f2364cb", first.R);
        Assert.Equal("0x472f77fea7d6fef10a210a7dd5293104cb6f78085be692461b6157a50649daaf", first.S);
        Assert.Equal(28, first.V);
    }

    [Fact]
    public void Every_signature_carries_a_recovery_id_and_a_low_s()
    {
        // Both are required rather than cosmetic. Without the recovery id the venue cannot work out which key
        // signed, since it is never told; without folding s into the lower half of the curve's order, half the
        // signatures are the high variant that a verifier following EIP-2 refuses - at random, from the caller's
        // point of view.
        byte[] key = Probe().PrivateKey!;
        for (long nonce = 1; nonce <= 40; nonce++)
        {
            HyperliquidSignature signature = HyperliquidSigner.Sign(
                HyperliquidActions.Cancel(0, (ulong)nonce).Packed, nonce, [], mainnet: true, key);

            Assert.InRange(signature.V, 27, 28);
            Assert.StartsWith("0x", signature.R, StringComparison.Ordinal);
            Assert.Equal(66, signature.R.Length);
            Assert.Equal(66, signature.S.Length);

            // The lower half of the order: its top byte is at most 0x7f, since the order's is 0xff.
            Assert.True(
                Convert.FromHexString(signature.S[2..])[0] <= 0x7f,
                $"the signature with nonce {nonce} has a high s, which a verifier following EIP-2 refuses");
        }
    }

    [Fact]
    public void The_request_body_carries_the_action_the_signature_was_made_over()
    {
        // Rebuilt from a parsed copy, the action in the body would be a second chance to differ from the bytes that
        // were hashed - so it goes in as the JSON the action produced itself.
        HyperliquidAction action = HyperliquidActions.Order([Limit()]);
        HyperliquidSignature signature = new("0xaa", "0xbb", 27);

        Assert.Equal(
            """{"action":{"type":"order","orders":[{"a":0,"b":true,"p":"10000","s":"0.001","r":false,"t":{"limit":{"tif":"Gtc"}}}],"grouping":"na"},"nonce":42,"signature":{"r":"0xaa","s":"0xbb","v":27}}""",
            HyperliquidHttp.ExchangeBody(action, 42L, signature));
    }

    [Fact]
    public void A_client_order_id_becomes_a_number_this_venue_can_carry()
    {
        // This venue's client order id is 128 bits and the engine's is a string of the caller's choosing, so the
        // engine's cannot be sent. A hash rather than a counter because it has to survive a restart: an order
        // placed before this process started has to be recognisable when it comes back, and recomputing the id from
        // the order in the cache does that where a table would not.
        string cloid = HyperliquidVenue.CloidFor(new ClientOrderId("O-20260925-000000-001-001-1"));

        Assert.StartsWith("0x", cloid, StringComparison.Ordinal);
        Assert.Equal((HyperliquidVenue.CloidBytes * 2) + 2, cloid.Length);

        // The same id always gives the same number, which is the whole point.
        Assert.Equal(cloid, HyperliquidVenue.CloidFor(new ClientOrderId("O-20260925-000000-001-001-1")));
        Assert.NotEqual(cloid, HyperliquidVenue.CloidFor(new ClientOrderId("O-20260925-000000-001-001-2")));
    }

    [Fact]
    public void The_venue_refuses_a_bad_signature_at_http_200_with_the_reason_in_the_body()
    {
        // Recorded from the live venue. The status says nothing, so a caller that checked it would read a refusal as
        // a success - and the two refusals mean completely different things: one is the digest, the other the
        // account.
        Assert.Contains("Unable to recover signer", HyperliquidPayloads.ExchangeBadSignature, StringComparison.Ordinal);
        Assert.Contains("does not exist", HyperliquidPayloads.ExchangeUnknownUser, StringComparison.Ordinal);

        // And the one that named the probe's own address, which is the evidence the encoding is right.
        Assert.Contains(ProbeAddress, HyperliquidPayloads.ExchangeUnknownUser, StringComparison.OrdinalIgnoreCase);
    }

    // ----- the actions the recorded bytes belong to -----

    private static HyperliquidOrderWire Limit() => new()
    {
        Asset = 0,
        IsBuy = true,
        Price = "10000",
        Size = "0.001",
        ReduceOnly = false,
        TimeInForce = HyperliquidActions.Gtc,
    };

    private static HyperliquidAction Action(string name) => name switch
    {
        "order" => HyperliquidActions.Order([Limit()]),
        "cancel" => HyperliquidActions.Cancel(0, 123456789UL),
        "cancelByCloid" => HyperliquidActions.CancelByCloid(0, "0x00000000000000000000000000000001"),
        "modify" => HyperliquidActions.Modify(123456789UL, Limit() with { Price = "11000", Size = "0.002" }),
        "updateLeverage/cross" => HyperliquidActions.UpdateLeverage(0, isCross: true, 5),
        "updateLeverage/isolated" => HyperliquidActions.UpdateLeverage(1, isCross: false, 3),
        "order/trigger" => HyperliquidActions.Order([new HyperliquidOrderWire
        {
            Asset = 0,
            IsBuy = false,
            Price = "80000",
            Size = "0.001",
            ReduceOnly = true,
            TriggerPrice = "80500",
            TriggerIsMarket = true,
            TriggerIsTakeProfit = false,
        }]),
        "order/cloid" => HyperliquidActions.Order([new HyperliquidOrderWire
        {
            Asset = 5,
            IsBuy = false,
            Price = "120.5",
            Size = "1.25",
            ReduceOnly = true,
            TimeInForce = HyperliquidActions.Alo,
            Cloid = "0x00000000000000000000000000000001",
        }]),
        "order/asset-233" => HyperliquidActions.Order([new HyperliquidOrderWire
        {
            Asset = 233,
            IsBuy = true,
            Price = "1",
            Size = "1",
            ReduceOnly = false,
            TimeInForce = HyperliquidActions.Ioc,
        }]),
        "cancel/large-oid" => HyperliquidActions.Cancel(233, 99999999999999UL),
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "no recorded action by that name"),
    };

    /// <summary>The packed bytes of a cancel as hex, whose asset index is the only integer in it that varies.</summary>
    private static string Packed(int asset) =>
        Convert.ToHexStringLower(HyperliquidActions.Cancel(asset, 1UL).Packed);

    private static byte[] Digest(string text) => HyperliquidSigner.Hash(Encoding.UTF8.GetBytes(text));
}

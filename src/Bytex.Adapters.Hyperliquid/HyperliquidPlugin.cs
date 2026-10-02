using Bytex.Core.Adapters;
using Bytex.Core.Model;
using Bytex.Core.Plugins;

namespace Bytex.Adapters.Hyperliquid;

/// <summary>
/// What this venue is, for a host that has never read this source.
/// <para>
/// This is the venue the declaration was hardest to fill in for, and the places it did not fit are worth naming
/// here rather than in a report nobody will find later.
/// </para>
/// <para>
/// THE KEY IS NOT A KEY. Every other venue here issues a key pair from a settings page, and this one issues nothing
/// at all: the credential is a secp256k1 private key that controls an address, and the address IS the account. So
/// the declaration's key has one secret part where the others have two or three, and a second part that is not a
/// secret at all - the account address, needed only when the signing key is an API wallet the account approved
/// rather than the account's own. <see cref="VenueKeyPart"/> carries a name, a variable and whether it is secret,
/// which is enough to hold this; what it cannot say is that the secret is a bearer of funds rather than a
/// revocable credential, so an API wallet and a wallet key look identical in the declaration and are not the same
/// thing to hold.
/// </para>
/// <para>
/// ONE FAMILY IS DECLARED AND THE VENUE HAS MORE. It runs a spot market - 330 pairs, measured - and it hosts
/// builder-deployed perpetual exchanges whose assets arrive as <c>xyz:AAPL</c>. Neither is declared, and the reason
/// is the declaration's own shape rather than a shortage of time: a family declares an <c>HttpBase</c> and a
/// <c>WsBase</c> and the settings that SELECT it, and this venue answers every market on one host, one path and one
/// socket. Two families here would be identical in every field a host reads and indistinguishable to the test that
/// checks a family's declared settings really select it. Spot would also arrive nameless: 329 of its 330 pairs are
/// called <c>@1</c> to <c>@868</c> by index, with exactly one - PURR/USDC - carrying a name.
/// </para>
/// <para>
/// AN ASSET IS AN INDEX. An order names its asset by position in a 234-entry array, not by a symbol, so the index
/// is carried on the instrument by the provider that read the array. Nothing in the declaration says that, and
/// nothing needs to - but it is why <c>LoadOneInstrument</c> is true for a venue with no per-instrument read at all.
/// </para>
/// </summary>
public sealed class HyperliquidPlugin : IPlugin, IVenuePlugin
{
    public string Id => "bytex.hyperliquid";

    public string Version => typeof(HyperliquidPlugin).Assembly.GetName().Version?.ToString() ?? "0";

    public VenueDescriptor Describe() => new()
    {
        Venue = HyperliquidVenue.Venue,
        DisplayName = "Hyperliquid",

        // None, and a programme that really exists. The venue runs builder codes: an order may carry a
        // `builder` object naming an address and a fee in tenths of a basis point, the account has to have
        // approved that builder's maximum fee with a signed action of its own, and `referral` reports what a
        // builder has earned under `builderRewards`. All three were measured with no key - `maxBuilderFee`
        // answered 0 for an unapproved pair, and the referral read named the reward field.
        //
        // So nothing here carries it, for the same reason KuCoin's cannot: the configured id is ONE string, and
        // this needs an address AND a fee rate, and the fee is bounded by an on-chain approval the user has to
        // have made separately. An id alone would be sent with no fee and earn nothing, or with a guessed fee and
        // be refused. Declared as a rebate going unclaimed rather than as a venue with nothing to claim.
        BrokerTag = BrokerTag.None,
        BrokerProgramme = BrokerProgramme.NotCarried,
        Families =
        [
            new VenueFamily
            {
                Name = "perpetuals",

                // Perpetuals and nothing else. Not a limit of the adapter and not inferred from a spelling: the
                // venue's `meta` read returns exactly one kind of contract, none of them dated and none of them
                // inverse, and the suffix this adapter puts on an instrument id is its own rather than the
                // venue's - the venue calls the asset "BTC" with no suffix at all.
                InstrumentClasses = [InstrumentClass.Swap],
                PaysFunding = true,
                Collateral = VenueCollateral.Quote,
                HttpBase = HyperliquidVenue.DefaultHttpBase,

                // A fixed address, unlike KuCoin's: one socket for the public channels AND for an account's own
                // orders and fills, with no token and no handshake. This is a root - the adapter appends /ws.
                //
                // There is a test network on the same shape, at api.hyperliquid-testnet.xyz and
                // wss://api.hyperliquid-testnet.xyz, and its universe was measured at 212 assets against
                // mainnet's 234. It is reached by setting baseUrlHttp and baseUrlWs, which is what those
                // settings are for, and the adapter takes the network from the host rather than from a separate
                // switch - so a client pointed at the test network signs test-network signatures and cannot
                // produce one that would be valid on mainnet. It is not a family of its own because it is the
                // same market: a family is what a venue OFFERS, not which copy of the venue is being talked to.
                WsBase = HyperliquidVenue.DefaultWsBase,
                Key = HyperliquidKey,

                // Empty: this venue has one family that the adapter offers, so nothing selects it. Every other
                // multi-market venue here needs a productType or an accountType; there is nothing to set.
                Config = new Dictionary<string, string>(StringComparer.Ordinal),

                // Fields the other venues need to select a market and this one has nowhere to put, because it
                // has one market that the adapter offers and nothing chooses it. A host carrying either of these
                // can tell a configuration that means nothing here from one that is wrong.
                IgnoredConfig = ["productType", "accountType"],

                // The base tier, read from the venue's own fee schedule with no key: 1.5 basis points to make
                // and 4.5 to take. Every account starts here and only volume moves it.
                DefaultFees = new VenueFees(HyperliquidVenue.BaseMakerFee, HyperliquidVenue.BaseTakerFee),

                // None. The venue publishes no archive of candles, trades or funding for download - and its
                // candle read serves only about the last five thousand bars of an interval, so there is no way
                // to assemble one either. That is a statement worth making: a host that wants deep history for
                // this venue has to buy it somewhere else.
                FreeDatasets = [],
                Capabilities = new VenueCapabilities
                {
                    // TRUE, and the capability that was hardest to answer honestly. There is no per-instrument
                    // read: `meta` takes no filter, measured - sent with a coin and a name field set to BTC it
                    // answered with all 234 assets and the same 17628 bytes as the bare request. So one
                    // instrument costs the whole catalog.
                    //
                    // It is still true, because this field is about what the ADAPTER can do and the adapter can:
                    // LoadAsync returns the one asked for and adds nothing else. Declaring it false would take
                    // this venue out of every add-instrument path and out of the family resolver, over a
                    // capability it demonstrably has. What the declaration has no field for is the COST - that
                    // this one is seventeen kilobytes where another venue's is two hundred bytes - and that is
                    // the gap, not the answer.
                    LoadOneInstrument = true,
                    ListInstruments = true,
                    BarHistory = true,
                    FundingHistory = true,
                    MarketData = true,

                    // True, and verified rather than hoped. The signature is EIP-712 typed data over a
                    // MessagePack encoding of the action, built here from the framework and nothing else, and
                    // it was checked against the live venue with a key nobody has funded: the venue names the
                    // address it recovered, and for every action this adapter sends - an order, an order with a
                    // trigger, a cancel by id, a cancel by client id, an amendment and a leverage change - it
                    // recovered exactly the signing address, on mainnet and on the test network. A mainnet
                    // signature replayed at the test network recovered a different address, which is the proof
                    // that the network letter in the digest does what it is there for.
                    Execution = true,

                    // True. The venue has a `modify` action that replaces the order: price, size, time in force,
                    // reduce-only and a trigger all travel again. It matches by the order's own numeric id, so
                    // an order that has not been given one cannot be amended - which the client says rather than
                    // silently cancelling and replacing.
                    AmendOrders = true,
                },
            },
        ],
    };

    /// <summary>
    /// What a credential for this venue is made of, which is not a key pair.
    /// <para>
    /// One secret and one address. The secret is a secp256k1 private key - the thing itself, never sent anywhere,
    /// with no counterpart the exchange holds and no page to revoke it from. The address is the account to read and
    /// trade, and it is OPTIONAL because it is only needed when the two differ: an API wallet the account approved
    /// signs on the account's behalf and has an address of its own, and every read on this venue is keyed by the
    /// account rather than by whoever signed. Left out, the account is the key's own address - right for a wallet
    /// key and silently wrong for an API wallet, where positions, orders and balances would all be read for an
    /// address that has never traded and every read would succeed and return nothing. So a host that never offers
    /// the field cannot fix that for a user, which is exactly why it is declared.
    /// </para>
    /// </summary>
    private static VenueKey HyperliquidKey => new()
    {
        Parts =
        [
            new VenueKeyPart("private key", HyperliquidVenue.EnvPrivateKey, Secret: true),
            new VenueKeyPart("account address", HyperliquidVenue.EnvAccountAddress, Secret: false, Required: false),
        ],
    };

    public void Register(IPluginRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.AddDataClientFactory(new HyperliquidDataClientFactory());
        registry.AddExecutionClientFactory(new HyperliquidExecutionClientFactory());
    }
}

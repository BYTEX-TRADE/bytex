using System.Text.Json;
using System.Text.Json.Serialization;
using Bytex.Core.Model;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Primitives;
using Bytex.Core.Serialization;

namespace Bytex.Data;

/// <summary>
/// JSON round-tripping of instrument definitions with a type discriminator.
/// </summary>
public static class InstrumentJson
{
    private sealed class Dto
    {
        public string Kind { get; set; } = string.Empty;

        public string Id { get; set; } = string.Empty;

        public string? RawSymbol { get; set; }

        public AssetClass AssetClass { get; set; }

        public InstrumentClass InstrumentClass { get; set; }

        public string QuoteCurrency { get; set; } = string.Empty;

        public string? BaseCurrency { get; set; }

        public string? SettlementCurrency { get; set; }

        public bool IsInverse { get; set; }

        public byte PricePrecision { get; set; }

        public byte SizePrecision { get; set; }

        public string PriceIncrement { get; set; } = "0";

        public string SizeIncrement { get; set; } = "0";

        public string? Multiplier { get; set; }

        public string? LotSize { get; set; }

        public string? MaxQuantity { get; set; }

        public string? MinQuantity { get; set; }

        public string? MaxNotional { get; set; }

        public string? MinNotional { get; set; }

        public string? MaxPrice { get; set; }

        public string? MinPrice { get; set; }

        public decimal MarginInit { get; set; }

        public decimal MarginMaint { get; set; }

        /// <summary>Absent for a venue that publishes its maximum leverage only to a key holder.</summary>
        public decimal? MaxLeverage { get; set; }

        public decimal MakerFee { get; set; }

        public decimal TakerFee { get; set; }

        public long TsEvent { get; set; }

        public long TsInit { get; set; }

        public Dictionary<string, string>? Info { get; set; }

        // Type-specific
        public string? Underlying { get; set; }

        public long? Activation { get; set; }

        public long? Expiration { get; set; }

        public string? Isin { get; set; }

        public string? Exchange { get; set; }

        public OptionKind? OptionKind { get; set; }

        public string? StrikePrice { get; set; }
    }

    private static readonly JsonSerializerOptions Options = new(BytexJson.Options) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public static string Serialize(Instrument instrument)
    {
        ArgumentNullException.ThrowIfNull(instrument);
        Dto dto = new()
        {
            Kind = instrument.GetType().Name,
            Id = instrument.Id.Value,
            RawSymbol = instrument.RawSymbol.Value,
            AssetClass = instrument.AssetClass,
            InstrumentClass = instrument.InstrumentClass,
            QuoteCurrency = instrument.QuoteCurrency.Code,
            BaseCurrency = instrument.BaseCurrency?.Code,
            SettlementCurrency = instrument.SettlementCurrency.Code,
            IsInverse = instrument.IsInverse,
            PricePrecision = instrument.PricePrecision,
            SizePrecision = instrument.SizePrecision,
            PriceIncrement = instrument.PriceIncrement.ToString(),
            SizeIncrement = instrument.SizeIncrement.ToString(),
            Multiplier = instrument.Multiplier.ToString(),
            LotSize = instrument.LotSize.ToString(),
            MaxQuantity = instrument.MaxQuantity?.ToString(),
            MinQuantity = instrument.MinQuantity?.ToString(),
            MaxNotional = instrument.MaxNotional?.ToString(),
            MinNotional = instrument.MinNotional?.ToString(),
            MaxPrice = instrument.MaxPrice?.ToString(),
            MinPrice = instrument.MinPrice?.ToString(),
            MarginInit = instrument.MarginInit,
            MarginMaint = instrument.MarginMaint,
            MaxLeverage = instrument.MaxLeverage,
            MakerFee = instrument.MakerFee,
            TakerFee = instrument.TakerFee,
            TsEvent = instrument.TsEvent.Value,
            TsInit = instrument.TsInit.Value,
            Info = instrument.Info.Count == 0 ? null : new Dictionary<string, string>(instrument.Info),
        };

        switch (instrument)
        {
            case CryptoFuture f:
                dto.Underlying = f.Underlying.Code;
                dto.Activation = f.Activation.Value;
                dto.Expiration = f.Expiration.Value;
                break;
            case Equity e:
                dto.Isin = e.Isin;
                break;
            case FuturesContract fc:
                dto.Underlying = fc.Underlying;
                dto.Activation = fc.Activation.Value;
                dto.Expiration = fc.Expiration.Value;
                dto.Exchange = fc.Exchange;
                break;
            case OptionContract oc:
                dto.Underlying = oc.Underlying;
                dto.Activation = oc.Activation.Value;
                dto.Expiration = oc.Expiration.Value;
                dto.Exchange = oc.Exchange;
                dto.OptionKind = oc.Kind;
                dto.StrikePrice = oc.StrikePrice.ToString();
                break;
        }

        return JsonSerializer.Serialize(dto, Options);
    }

    public static Instrument Deserialize(string json)
    {
        Dto dto = JsonSerializer.Deserialize<Dto>(json, Options) ?? throw new FormatException("Invalid instrument JSON.");
        InstrumentSpec spec = new()
        {
            Id = InstrumentId.Parse(dto.Id),
            RawSymbol = dto.RawSymbol is null ? null : new Symbol(dto.RawSymbol),
            AssetClass = dto.AssetClass,
            InstrumentClass = dto.InstrumentClass,
            QuoteCurrency = Currency.FromCode(dto.QuoteCurrency),
            BaseCurrency = dto.BaseCurrency is null ? null : Currency.FromCode(dto.BaseCurrency),
            SettlementCurrency = dto.SettlementCurrency is null ? null : Currency.FromCode(dto.SettlementCurrency),
            IsInverse = dto.IsInverse,
            PricePrecision = dto.PricePrecision,
            SizePrecision = dto.SizePrecision,
            PriceIncrement = Price.Parse(dto.PriceIncrement),
            SizeIncrement = Quantity.Parse(dto.SizeIncrement),
            Multiplier = dto.Multiplier is null ? null : Quantity.Parse(dto.Multiplier),
            LotSize = dto.LotSize is null ? null : Quantity.Parse(dto.LotSize),
            MaxQuantity = dto.MaxQuantity is null ? null : Quantity.Parse(dto.MaxQuantity),
            MinQuantity = dto.MinQuantity is null ? null : Quantity.Parse(dto.MinQuantity),
            MaxNotional = dto.MaxNotional is null ? null : Money.Parse(dto.MaxNotional),
            MinNotional = dto.MinNotional is null ? null : Money.Parse(dto.MinNotional),
            MaxPrice = dto.MaxPrice is null ? null : Price.Parse(dto.MaxPrice),
            MinPrice = dto.MinPrice is null ? null : Price.Parse(dto.MinPrice),
            MarginInit = dto.MarginInit,
            MarginMaint = dto.MarginMaint,
            MaxLeverage = dto.MaxLeverage,
            MakerFee = dto.MakerFee,
            TakerFee = dto.TakerFee,
            TsEvent = new UnixNanos(dto.TsEvent),
            TsInit = new UnixNanos(dto.TsInit),
            Info = dto.Info,
        };

        return dto.Kind switch
        {
            nameof(CurrencyPair) => new CurrencyPair(spec),
            nameof(CryptoPerpetual) => new CryptoPerpetual(spec),
            nameof(CryptoFuture) => new CryptoFuture(spec, Currency.FromCode(dto.Underlying ?? dto.BaseCurrency ?? dto.QuoteCurrency), new UnixNanos(dto.Activation ?? 0), new UnixNanos(dto.Expiration ?? 0)),
            nameof(Equity) => new Equity(spec, dto.Isin),
            nameof(FuturesContract) => new FuturesContract(spec, dto.Underlying ?? string.Empty, new UnixNanos(dto.Activation ?? 0), new UnixNanos(dto.Expiration ?? 0), dto.Exchange),
            nameof(OptionContract) => new OptionContract(spec, dto.Underlying ?? string.Empty, dto.OptionKind ?? OptionKind.Call, Price.Parse(dto.StrikePrice ?? "0"), new UnixNanos(dto.Activation ?? 0), new UnixNanos(dto.Expiration ?? 0), dto.Exchange),
            _ => throw new FormatException($"Unknown instrument kind '{dto.Kind}'."),
        };
    }
}

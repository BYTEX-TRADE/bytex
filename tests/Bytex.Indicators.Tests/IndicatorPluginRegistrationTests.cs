using Bytex.Core.Indicators;
using Bytex.Core.Plugins;
using Bytex.Indicators.Tests.Support;

namespace Bytex.Indicators.Tests;

// docs/concepts/indicators.md: "plugins may register their own IIndicatorFactory".
// The registry resolves a name by asking the registered factories in registration order.
public class IndicatorPluginRegistrationTests
{
    [Fact]
    public void The_builtin_factory_registered_in_a_registry_creates_indicators_by_name()
    {
        PluginRegistry registry = new();
        registry.AddIndicatorFactory(new BuiltinIndicatorFactory());

        IIndicator indicator = registry.CreateIndicator("atr", new Dictionary<string, string> { ["period"] = "5" });

        Assert.IsType<AverageTrueRange>(indicator);
        Assert.Equal("ATR(5)", indicator.Name);
    }

    [Fact]
    public void A_plugin_contributes_its_own_indicators_through_the_plugin_contract()
    {
        PluginRegistry registry = new();
        registry.AddIndicatorFactory(new BuiltinIndicatorFactory());

        registry.AddPlugin(new LastPricePlugin());

        IIndicator custom = registry.CreateIndicator("LASTPRICE", new Dictionary<string, string>());
        Assert.IsType<LastPrice>(custom);
        Assert.IsType<SimpleMovingAverage>(registry.CreateIndicator("SMA", new Dictionary<string, string>()));
        Assert.Equal(2, registry.IndicatorFactories.Count);
    }

    [Fact]
    public void A_custom_indicator_only_has_to_implement_UpdateRaw_to_accept_bars_quotes_and_trades()
    {
        LastPrice indicator = new();

        indicator.Update(Make.Bar(high: 12m, low: 8m, close: 11m));
        Assert.Equal(11m, indicator.Value);

        indicator.Update(Make.Trade(price: 13m));
        Assert.Equal(13m, indicator.Value);

        indicator.Update(Make.Quote(bid: 14m, ask: 16m));
        Assert.Equal(15m, indicator.Value);
    }

    [Fact]
    public void When_two_factories_offer_the_same_name_the_first_registered_wins()
    {
        PluginRegistry registry = new();
        registry.AddIndicatorFactory(new LastPriceFactory("SMA"));
        registry.AddIndicatorFactory(new BuiltinIndicatorFactory());

        Assert.IsType<LastPrice>(registry.CreateIndicator("SMA", new Dictionary<string, string>()));
    }

    [Fact]
    public void A_name_nobody_offers_is_rejected_and_named_in_the_message()
    {
        PluginRegistry registry = new();
        registry.AddIndicatorFactory(new BuiltinIndicatorFactory());

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => registry.CreateIndicator("ICHIMOKU", new Dictionary<string, string>()));

        Assert.Contains("ICHIMOKU", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_null_factory_is_rejected()
    {
        Assert.Throws<ArgumentNullException>(() => new PluginRegistry().AddIndicatorFactory(null!));
    }

    private sealed class LastPrice : Indicator
    {
        public LastPrice()
            : base("LASTPRICE")
        {
        }

        public decimal Value { get; private set; }

        public override void UpdateRaw(decimal value)
        {
            Value = value;
            HasInputs = true;
            IsInitialized = true;
        }
    }

    private sealed class LastPriceFactory : IIndicatorFactory
    {
        public LastPriceFactory(string name)
        {
            Names = [name];
        }

        public IReadOnlyList<string> Names { get; }

        public IIndicator Create(string name, IReadOnlyDictionary<string, string> parameters) => new LastPrice();
    }

    private sealed class LastPricePlugin : IPlugin
    {
        public string Id => "tests.last-price";

        public string Version => "1.0.0";

        public void Register(IPluginRegistry registry) => registry.AddIndicatorFactory(new LastPriceFactory("LASTPRICE"));
    }
}

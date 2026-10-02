using Bytex.Live.Network;

namespace Bytex.Live.Tests.Network;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class EnvironmentCollection
{
    public const string Name = "Process environment";
}

// Why: credentials may come from configuration or from the environment; the precedence must be explicit and
// a missing credential must fail loudly, naming the variable to set and nothing else.
[Collection(EnvironmentCollection.Name)]
public sealed class SecretsTests : IDisposable
{
    private readonly string _variable = "BYTEX_TEST_SECRET_" + Guid.NewGuid().ToString("N");

    public void Dispose() => Environment.SetEnvironmentVariable(_variable, null);

    [Fact]
    public void Require_prefers_the_explicit_value_over_the_environment()
    {
        Environment.SetEnvironmentVariable(_variable, "from-env");

        Assert.Equal("from-config", Secrets.Require("from-config", _variable));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Require_falls_back_to_the_environment_when_the_value_is_blank(string? configured)
    {
        Environment.SetEnvironmentVariable(_variable, "from-env");

        Assert.Equal("from-env", Secrets.Require(configured, _variable));
    }

    [Fact]
    public void Require_throws_naming_the_variable_when_neither_source_has_a_value()
    {
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => Secrets.Require(null, _variable));

        Assert.Contains(_variable, error.Message);
    }

    [Fact]
    public void Optional_returns_null_when_neither_source_has_a_value()
    {
        Assert.Null(Secrets.Optional(null, _variable));
    }

    [Fact]
    public void Optional_uses_the_same_precedence_as_Require()
    {
        Environment.SetEnvironmentVariable(_variable, "from-env");

        Assert.Equal("from-config", Secrets.Optional("from-config", _variable));
        Assert.Equal("from-env", Secrets.Optional(" ", _variable));
    }
}

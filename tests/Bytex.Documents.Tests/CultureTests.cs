using Bytex.Backtest;
using Bytex.Documents.Runtime;
using Bytex.Documents.Schema;
using Bytex.Documents.Validation;

namespace Bytex.Documents.Tests;

// Why: a strategy document is a file people copy between machines and a host writes back. Every number in it is stored
// as text - a price, a size, a parameter's value, its minimum and maximum - so the culture of whoever saved it decides
// whether the file still means the same thing when somebody else opens it. The same is true of the decisions a run
// emits, which carry numbers a host displays.
//
// The repository builds with InvariantGlobalization, so the current culture IS the invariant one in every test;
// CommaDecimalCulture builds the hostile one by hand and proves it took.
public sealed class CultureTests
{
    [Fact]
    public void A_document_saved_under_a_comma_culture_is_the_same_document()
    {
        StrategyDocument document = Fixtures.Example("breakout-retest");
        string invariant = DocumentJson.Serialize(document);

        using (new CommaDecimalCulture())
        {
            string hostile = DocumentJson.Serialize(document);

            Assert.Equal(invariant, hostile);

            // And reading it back gives the same document rather than one whose numbers moved by three orders of
            // magnitude, which is what a comma read as a thousands separator does.
            StrategyDocument restored = DocumentJson.Deserialize(invariant);

            Assert.Equal(invariant, DocumentJson.Serialize(restored));
        }
    }

    [Fact]
    public void A_document_validates_the_same_under_a_comma_culture()
    {
        // The validator parses every parameter's value, minimum, maximum and step out of the document's text, and
        // compares them. A parse that read "0.5" as 5 would refuse a document that is correct, or accept one that
        // is not - and the message would name a number nobody wrote.
        StrategyDocument document = Fixtures.Example("support-bounce");
        ValidationReport invariant = new DocumentValidator().Validate(document);

        using (new CommaDecimalCulture())
        {
            ValidationReport hostile = new DocumentValidator().Validate(document);

            Assert.Equal(invariant.IsValid, hostile.IsValid);
            Assert.Equal(
                invariant.Findings.Select(f => f.Code + "|" + f.Message),
                hostile.Findings.Select(f => f.Code + "|" + f.Message));
        }

        Assert.True(invariant.IsValid, "the example itself is meant to be valid");
    }

    [Fact]
    public void A_run_under_a_comma_culture_decides_the_same_things_and_says_them_the_same_way()
    {
        // End to end: the same document, the same bars, the same orders - and the same decision text, which is what a
        // host shows somebody and what the node control channel hands out.
        StrategyDocument document = Fixtures.Example("ema-cross");
        (BacktestResult invariantResult, DocumentStrategy invariantStrategy) = Fixtures.RunBacktest(document, bars: 600);
        string[] invariantDecisions = [.. invariantStrategy.Decisions.Select(d => d.Kind + "|" + d.NodeId + "|" + d.Message)];

        using (new CommaDecimalCulture())
        {
            (BacktestResult hostileResult, DocumentStrategy hostileStrategy) = Fixtures.RunBacktest(document, bars: 600);

            Assert.Equal(Fixtures.Fingerprint(invariantResult), Fixtures.Fingerprint(hostileResult));
            string[] hostileDecisions = [.. hostileStrategy.Decisions.Select(d => d.Kind + "|" + d.NodeId + "|" + d.Message)];

            Assert.Equal(invariantDecisions, hostileDecisions);
        }

        Assert.NotEmpty(invariantDecisions);
    }
}

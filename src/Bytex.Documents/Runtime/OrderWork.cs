namespace Bytex.Documents.Runtime;

/// <summary>
/// The names and bounds of the <c>work</c> parameter: how an order reaches the market. Left alone, an order goes to
/// the venue in one piece, which is what every order has always done; worked, it is handed to an execution algorithm
/// that cuts it up and sends the pieces on a pace.
/// <para>
/// They are constants because four places have to agree on the same words: the catalog that offers the choice, the
/// node that turns it into an order, the validator that refuses an order type no algorithm can work, and the strategy
/// that has to see to it that the algorithm the document names is running.
/// </para>
/// </summary>
public static class OrderWork
{
    /// <summary>The object parameter that holds all of it.</summary>
    public const string Param = "work";

    /// <summary>The field naming what works the order.</summary>
    public const string Algorithm = "algorithm";

    /// <summary>Send it as it is, in one piece: the default, and what a document written before any of this did.</summary>
    public const string None = "none";

    /// <summary>Work it as the time-weighted average price: equal pieces at an even pace over the horizon.</summary>
    public const string Twap = "twap";

    /// <summary>The field holding how long the whole order is worked over.</summary>
    public const string HorizonMinutes = "horizonMinutes";

    /// <summary>The field holding how often a piece goes out.</summary>
    public const string IntervalMinutes = "intervalMinutes";

    /// <summary>What the horizon is when the document does not say: long enough to matter, short enough to end.</summary>
    public const decimal DefaultHorizonMinutes = 5m;

    /// <summary>What the interval is when the document does not say: five pieces over the default horizon.</summary>
    public const decimal DefaultIntervalMinutes = 1m;

    /// <summary>Six seconds, the shortest either of them may be: below this the pace is the venue's rate limit, not a strategy.</summary>
    public const decimal MinMinutes = 0.1m;

    /// <summary>A day, the longest either of them may be: an order worked over more than that is a position, not an order.</summary>
    public const decimal MaxMinutes = 1440m;

    /// <summary>Half a minute, what an editor's arrows move by.</summary>
    public const decimal StepMinutes = 0.5m;

    /// <summary>The order types an algorithm can work: the two that say what they are willing to pay, or that they are not asking.</summary>
    public static readonly IReadOnlyList<string> WorkableOrderTypes = ["market", "limit"];
}

using Bytex.Core.Model.Identifiers;

namespace Bytex.Core.Adapters;

/// <summary>
/// What a venue that matches orders itself - a paper venue, a simulated one - is matching them against right now.
/// </summary>
/// <remarks>
/// A paper fill is the answer to "would this have filled, and at what", and the answer is worth very different
/// amounts depending on what it was measured against: a book says where in the queue the order stood and what the
/// depth cost, a quote says only what was on offer at the touch, and a bar says what traded over a whole minute. A
/// venue that can match against a book when one arrives and against bars when one has not must be able to say which
/// it did, or nobody reading a fill knows what they are reading.
/// </remarks>
public interface IMatchingReport
{
    /// <summary>What this venue is matching each instrument it holds against, one entry an instrument.</summary>
    IReadOnlyList<MatchingAgainst> Matching();
}

/// <summary>
/// What one instrument's orders are being matched against at this venue.
/// </summary>
/// <param name="InstrumentId">The instrument.</param>
/// <param name="Against">
/// <c>book</c>, <c>quotes</c> or <c>bars</c>: the best thing the venue has for this instrument right now. Not what it
/// was configured to want - what it has.
/// </param>
/// <param name="WantsBook">Whether this venue was told to match against a book at all.</param>
/// <param name="BookRequested">Whether it has asked its data client for the book of this instrument.</param>
public readonly record struct MatchingAgainst(InstrumentId InstrumentId, string Against, bool WantsBook, bool BookRequested)
{
    /// <summary>Matching against a maintained order book: depth, and a queue position for what rests.</summary>
    public const string Book = "book";

    /// <summary>Matching against the touch of a quote: what was on offer at the best price, and nothing behind it.</summary>
    public const string Quotes = "quotes";

    /// <summary>Matching against bars: a share of what traded over each bar's whole length.</summary>
    public const string Bars = "bars";

    /// <summary>Nothing has arrived for this instrument yet, so nothing would match.</summary>
    public const string Nothing = "nothing";
}

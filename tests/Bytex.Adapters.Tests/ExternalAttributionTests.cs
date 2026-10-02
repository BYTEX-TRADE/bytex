using System.Text.RegularExpressions;
using Bytex.Adapters.Tests.Support;

namespace Bytex.Adapters.Tests;

// Why: a venue's private stream carries everything that happens to an ACCOUNT, not everything that happens to this
// node's orders. A person trading the same key by hand, another node on the same key, an order left behind by a
// previous run - all of it arrives on the same socket.
//
// Ten of this engine's twelve stream handlers used to report those under a made-up "EXTERNAL" strategy. That looks
// like a decision and is not one: OrderCoordinator looks a stream event up by client order id and then by venue order
// id, and finding neither it logs "for unknown order" and DROPS the event. So the attribution never produced an
// order, a position or a fill - only a log line about somebody else's order. Worse, it never reached a strategy that
// had CLAIMED the instrument either, because external order claims are honoured in reconciliation and a stream
// handler bypasses reconciliation entirely.
//
// Two handlers, Kraken's, refused instead, and one of those refusals is why CI went red: the two conventions
// disagreed and both were tested. This file is the ruling, kept where it cannot drift: the attribution belongs to
// reconciliation, and an adapter may only name it when it is answering something the engine asked.
//
// docs/concepts/live.md documents the reconciliation path; Bytex.Core's OrderCoordinator implements it.
public sealed class ExternalAttributionTests
{
    /// <summary>The engine's commands. A method taking one of these is answering the engine, not the venue.</summary>
    private static readonly string[] _commands =
    [
        "SubmitOrder",
        "SubmitOrderList",
        "ModifyOrder",
        "CancelOrder",
        "CancelAllOrders",
        "BatchCancelOrders",
        "QueryOrder",
    ];

    /// <summary>How the attribution can be spelled: through the constant, or by writing the identifier out.</summary>
    private static readonly Regex _spelled = new(
        @"StrategyId\.External|new StrategyId\(""EXTERNAL""\)",
        RegexOptions.None,
        TimeSpan.FromSeconds(5));

    /// <summary>
    /// Only a reply to a command may attribute anything to the EXTERNAL strategy.
    ///
    /// <para>
    /// A reply needs a strategy id for an order the cache has lost - a rejection of an amendment has to be stamped
    /// with something, and the engine correlates it by the command rather than by the id - so the attribution is
    /// harmless there. On a stream message it is not: nothing in this node placed that order, the engine drops the
    /// event, and a claim never gets a look in.
    /// </para>
    ///
    /// <para>
    /// Read off disk rather than from a list, so the eleventh adapter is held to this the day it lands.
    /// </para>
    /// </summary>
    [Fact]
    public void Only_a_reply_to_a_command_may_name_the_EXTERNAL_strategy()
    {
        List<string> offences = [];

        foreach (string venue in Repo.ShippedVenues())
        {
            foreach (string file in Repo.SourceFiles(venue))
            {
                string[] lines = File.ReadAllLines(file);
                for (int i = 0; i < lines.Length; i++)
                {
                    if (!_spelled.IsMatch(lines[i]))
                    {
                        continue;
                    }

                    string signature = EnclosingSignature(lines, i);
                    if (!_commands.Any(c => signature.Contains(c + " ", StringComparison.Ordinal)))
                    {
                        offences.Add($"{Path.GetFileName(file)}:{i + 1} in {signature.Trim()}");
                    }
                }
            }
        }

        Assert.Empty(offences);
    }

    /// <summary>
    /// And the rule the refusal replaced it with, stated once: an adapter asks whether this node placed the order.
    ///
    /// <para>
    /// Both ids, and that is the half worth guarding. A venue may echo a client order id it was not sent - a broker
    /// prefix, a venue that rewrites ids - and the engine keeps an index by venue order id for exactly that, so an
    /// adapter refusing on the client id alone would throw away events for its OWN orders. That is a worse failure
    /// than the noise the refusal set out to remove, and it is invisible: the order simply never progresses.
    /// </para>
    /// </summary>
    [Fact]
    public void Every_execution_client_asks_whether_this_node_placed_the_order()
    {
        List<string> missing = [];

        foreach (string venue in Repo.ShippedVenues())
        {
            string[] clients = [.. Repo.SourceFiles(venue).Where(f => Path.GetFileName(f).EndsWith("ExecutionClient.cs", StringComparison.Ordinal))];
            foreach (string client in clients)
            {
                string text = File.ReadAllText(client);

                // A client with no private stream has nothing to refuse: the paper clients, and the delivery client
                // that only forwards commands. A stream is what the handlers read, so that is what is asked about.
                if (!text.Contains("private void Handle", StringComparison.Ordinal))
                {
                    continue;
                }

                if (!text.Contains("OrderPlacedHere(", StringComparison.Ordinal))
                {
                    missing.Add(Path.GetFileName(client));
                }
            }
        }

        Assert.Empty(missing);
    }

    /// <summary>The declaration the line sits in, accumulated until its parentheses balance.</summary>
    private static string EnclosingSignature(string[] lines, int index)
    {
        for (int i = index; i >= 0; i--)
        {
            string line = lines[i];
            if (!Declaration(line))
            {
                continue;
            }

            string signature = line;
            int depth = Depth(line);
            for (int j = i + 1; depth > 0 && j < lines.Length; j++)
            {
                signature += lines[j];
                depth += Depth(lines[j]);
            }

            return signature;
        }

        return "(no enclosing declaration)";
    }

    private static bool Declaration(string line) =>
        line.StartsWith("    public ", StringComparison.Ordinal)
        || line.StartsWith("    private ", StringComparison.Ordinal)
        || line.StartsWith("    protected ", StringComparison.Ordinal)
        || line.StartsWith("    internal ", StringComparison.Ordinal);

    private static int Depth(string line) => line.Count(c => c == '(') - line.Count(c => c == ')');
}

using System.Text.RegularExpressions;

namespace Bytex.Cli.Tests;

// Why: a suite this size is itself a thing that can rot, and it rots quietly - a test that asserts nothing, a test
// switched off, a test that waits a fixed number of milliseconds and fails on a loaded machine for a reason that was
// never the code's fault. None of that shows up as a failure; it shows up as confidence.
//
// A sweep of all 2,387 declarations found: nothing asserting nothing, nothing switched off, one test asserting only
// that something was not null, and three bare sleeps of which one was a genuine guess. This keeps it that way.
//
// It reads the test tree as text on purpose. Reflection would see compiled tests and could not tell a sleep from a
// deadline, and the questions here are about how a test is written rather than what it does.
public sealed class TestSuiteAuditTests
{
    /// <summary>
    /// The sleeps that earn their place: a test proving that something does NOT happen has to wait some bounded time
    /// to say so, and there is no way round it. Each entry is a claim that the wait is the assertion rather than a
    /// guess about how long work takes. Anything not listed has to wait for a condition instead.
    /// </summary>
    private static readonly Dictionary<string, string> _boundedNegatives = new(StringComparer.Ordinal)
    {
        ["The_node_keeps_checking_itself_against_its_venues_while_it_runs"] =
            "proves the checking STOPS: two samples an interval apart, the second the same as the first",
        ["A_node_that_was_not_asked_to_keep_checking_checks_once"] =
            "proves no SECOND check arrives, after waiting for the first",
        ["A_check_is_skipped_while_an_order_is_in_flight"] =
            "proves no check happens across several intervals while an order is unacknowledged",
        ["On_a_market_that_has_not_traded_since_the_subscription_bars_start_flat_from_the_last_closed_bar"] =
            "proves a bar is emitted on a timer with no trade to trigger it",
        ["A_full_queue_blocks_the_poster_until_the_loop_makes_room_and_loses_nothing"] =
            "the delay is the timeout arm of a WhenAny: the assertion is that the post did NOT complete while full",
        ["StopAsync_gives_up_after_its_timeout_and_discards_work_that_is_still_queued"] =
            "the delay is the timeout arm of a WhenAny bounding a stop that is meant not to finish",
        ["Request_that_fails_in_the_client_is_answered_with_the_reason"] =
            "polls for the answer with a deadline",
    };

    private static DirectoryInfo Repository()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "tests", "Bytex.Core.Tests")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return dir!;
    }

    private static IEnumerable<(string File, string Name, string Attribute, string Body)> Tests()
    {
        foreach (FileInfo file in new DirectoryInfo(Path.Combine(Repository().FullName, "tests")).GetFiles("*.cs", SearchOption.AllDirectories))
        {
            string path = file.FullName;
            if (path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                continue;
            }

            string text = File.ReadAllText(path);
            string relative = Path.GetRelativePath(Repository().FullName, path).Replace(Path.DirectorySeparatorChar, '/');
            foreach (Match attribute in Regex.Matches(text, @"\[(Fact|Theory)[^\]]*\]"))
            {
                Match method = Regex.Match(text[attribute.Index..], @"(?:public|internal|private)\s+(?:async\s+)?[\w<>\[\],\s\?]+\s+(?<name>\w+)\s*\(");
                if (!method.Success)
                {
                    continue;
                }

                int from = attribute.Index + method.Index + method.Length;
                yield return (relative, method.Groups["name"].Value, attribute.Value, Body(text, from));
            }
        }
    }

    /// <summary>The braces-balanced body that follows an index, so a nested block cannot end the method early.</summary>
    private static string Body(string text, int from)
    {
        int open = text.IndexOf('{', from);
        if (open < 0)
        {
            return string.Empty;
        }

        int depth = 0;
        for (int i = open; i < text.Length; i++)
        {
            depth += text[i] switch { '{' => 1, '}' => -1, _ => 0 };
            if (depth == 0)
            {
                return text[open..(i + 1)];
            }
        }

        return text[open..];
    }

    [Fact]
    public void The_sweep_finds_the_suite_it_is_meant_to_sweep()
    {
        // A walk that quietly found nothing would turn every audit below into a pass, which is the failure this
        // whole file exists to prevent.
        List<(string File, string Name, string Attribute, string Body)> all = Tests().ToList();
        Assert.True(all.Count > 2_000, $"the sweep found only {all.Count} test declarations");
        Assert.All(all, t => Assert.False(string.IsNullOrWhiteSpace(t.Body), $"{t.File}::{t.Name} has no body the sweep can read"));
    }

    [Fact]
    public void No_test_is_switched_off()
    {
        string[] skipped = Tests()
            .Where(t => t.Attribute.Contains("Skip", StringComparison.Ordinal))
            .Select(t => $"{t.File}::{t.Name}")
            .ToArray();

        Assert.True(
            skipped.Length == 0,
            $"tests are switched off, so their claims are unchecked: {string.Join(", ", skipped)}. "
            + "A skipped test that holds the right expectation for a known defect belongs in the changelog with the "
            + "defect, not in a green suite.");
    }

    [Fact]
    public void No_test_passes_without_asserting_something()
    {
        // Counted through one level of helper, because a shared helper that asserts is a good pattern - the first
        // version of this sweep flagged 68 of them and every one was wrong.
        Dictionary<string, List<string>> helpers = new(StringComparer.Ordinal);
        foreach (FileInfo file in new DirectoryInfo(Path.Combine(Repository().FullName, "tests")).GetFiles("*.cs", SearchOption.AllDirectories))
        {
            if (file.FullName.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || file.FullName.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                continue;
            }

            string text = File.ReadAllText(file.FullName);
            foreach (Match method in Regex.Matches(text, @"(?:public|internal|private)\s+(?:static\s+)?(?:async\s+)?[\w<>\[\],\s\?]+\s+(?<name>\w+)\s*\("))
            {
                string name = method.Groups["name"].Value;
                (helpers.TryGetValue(name, out List<string>? list) ? list : helpers[name] = new List<string>())
                    .Add(Body(text, method.Index + method.Length));
            }
        }

        bool Asserts(string body, int depth)
        {
            if (Regex.IsMatch(body, @"\bAssert\.|\bAssertDenied|\bThrows"))
            {
                return true;
            }

            return depth < 2
                && Regex.Matches(body, @"\b([A-Z]\w+)\s*\(")
                    .Select(m => m.Groups[1].Value)
                    .Distinct(StringComparer.Ordinal)
                    .Any(call => helpers.TryGetValue(call, out List<string>? bodies) && bodies.Any(b => Asserts(b, depth + 1)));
        }

        string[] silent = Tests()
            .Where(t => !Asserts(t.Body, 0))
            .Select(t => $"{t.File}::{t.Name}")
            .ToArray();

        Assert.True(
            silent.Length == 0,
            $"tests that assert nothing, so they pass as long as nothing throws: {string.Join(", ", silent)}. "
            + "That is not coverage, it is the appearance of it.");
    }

    [Fact]
    public void A_test_waits_for_a_condition_rather_than_a_number_of_milliseconds()
    {
        // A fixed sleep waiting FOR something is a guess: too short and it fails on a loaded machine for a reason
        // that was never the code's fault, too long and it slows every run for ever. Waiting for the condition is
        // always available. The exception is proving something does NOT happen, which needs a bound - and those are
        // listed above with the reason each one earns.
        List<string> guesses = new();
        foreach ((string file, string name, _, string body) in Tests())
        {
            if (!Regex.IsMatch(body, @"Task\.Delay|Thread\.Sleep") || _boundedNegatives.ContainsKey(name))
            {
                continue;
            }

            // A delay inside a polling loop is the right pattern: it is waiting for a condition with a deadline.
            bool polls = Regex.IsMatch(body, @"while\s*\(|WaitUntil|WhenAny");
            if (!polls)
            {
                guesses.Add($"{file}::{name}");
            }
        }

        Assert.True(
            guesses.Count == 0,
            $"tests that sleep for a fixed time instead of waiting for what they are waiting for: "
            + $"{string.Join(", ", guesses)}. Wait for the condition with a deadline, or - if the test proves "
            + "something does NOT happen - add it to the list in this file with the reason the bound is the "
            + "assertion.");
    }

    [Fact]
    public void Every_listed_bounded_negative_still_exists_and_still_sleeps()
    {
        // An allow-list rots the moment it outlives what it excused. A name that has been renamed or a test that no
        // longer sleeps must come off the list, or the list starts hiding the next one.
        Dictionary<string, string> sleeping = Tests()
            .Where(t => Regex.IsMatch(t.Body, @"Task\.Delay|Thread\.Sleep"))
            .GroupBy(t => t.Name, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().File, StringComparer.Ordinal);

        string[] stale = _boundedNegatives.Keys.Where(name => !sleeping.ContainsKey(name)).ToArray();

        Assert.True(
            stale.Length == 0,
            $"the list of sleeps that earn their place names tests that no longer sleep or no longer exist: "
            + $"{string.Join(", ", stale)}. Take them off it.");
    }
}

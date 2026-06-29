using System.Collections.Generic;
using AurumTweaks.Models;

namespace AurumTweaks.Services;

/// <summary>
/// Pure bounded-append + trend synthesis for the optimization-score timeline — no I/O, so the load-bearing rules
/// are unit-testable without a store:
/// <list type="bullet">
/// <item><b>An unchanged score isn't recorded.</b> Re-measuring a machine that didn't move adds no information and
/// would bloat the timeline with identical points — so <see cref="Record"/> returns the SAME reference when the new
/// score equals the most recent one, letting the store detect the no-op (by reference) and skip a pointless write.
/// A side-effect the user wants: the "depuis le …" date then reflects the last time the score actually changed,
/// not merely the last time the app was opened.</item>
/// <item><b>The history is bounded.</b> It keeps at most <see cref="MaxSamples"/> points, dropping the oldest — a
/// timeline that grew without limit would eventually be the thing that fills the disk (the <see cref="JournalLog"/>
/// rule, applied to the score).</item>
/// </list>
/// <see cref="Summarize"/> compares only the two most recent points and never fabricates a trend from one sample.
/// Samples are stored oldest-first (newest at the tail), so the latest reading is always <c>[^1]</c>.
/// </summary>
public static class ScoreHistory
{
    public const int MaxSamples = 60;

    public static IReadOnlyList<ScoreSnapshot> Record(IReadOnlyList<ScoreSnapshot> existing, ScoreSnapshot sample,
                                                      int cap = MaxSamples)
    {
        // A repeated identical score adds no information — return the same instance so the store skips the write.
        if (existing.Count > 0 && existing[existing.Count - 1].Score == sample.Score)
            return existing;

        var list = new List<ScoreSnapshot>(existing.Count + 1);
        list.AddRange(existing);
        list.Add(sample);
        if (cap >= 0 && list.Count > cap)
            list.RemoveRange(0, list.Count - cap);   // drop the oldest head, keep the newest `cap`
        return list;
    }

    public static ScoreProgress Summarize(IReadOnlyList<ScoreSnapshot> samples)
    {
        // One point is a value, not a trend — refuse to imply a direction we can't honestly show.
        if (samples.Count < 2) return ScoreProgress.None;
        var latest = samples[samples.Count - 1];
        var previous = samples[samples.Count - 2];
        return new ScoreProgress(true, latest.Score, previous.Score, latest.Score - previous.Score,
                                 previous.TimestampUtc);
    }
}

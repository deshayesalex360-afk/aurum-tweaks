using System;
using System.Text.Json.Serialization;

namespace AurumTweaks.Models;

/// <summary>
/// One persisted point on the optimization-score timeline: a UTC instant and the 0-100 score read then. The
/// history is what lets the dashboard say « 82 (+13 depuis la dernière mesure) » instead of a single context-free
/// number — the score's progression over time, recorded only when detection produced a real (verifiable) score.
/// Pure data + a display-only local label; the bounded-append policy and the JSON store live in Services.
/// </summary>
public sealed record ScoreSnapshot(DateTime TimestampUtc, int Score)
{
    /// <summary>Local wall-clock date for a timeline row (display only — the stored instant is always UTC).</summary>
    [JsonIgnore] public string LocalDateLabel => TimestampUtc.ToLocalTime().ToString("dd/MM/yyyy");
}

/// <summary>
/// The read-only "where the score went" synthesis between the two most recent samples: the current value, the
/// previous one, the signed delta, and when that previous reading was taken. Built by
/// <see cref="AurumTweaks.Services.ScoreHistory.Summarize"/> as a pure comparison of what was recorded — it never
/// fabricates a trend from a single point (<see cref="HasTrend"/> stays false until two real samples exist), and
/// it states a delta of 0 as honest stability, not a vague "no change". Never persisted, so no JSON attributes
/// (mirrors <see cref="JournalStatistics"/>).
/// </summary>
public sealed record ScoreProgress(bool HasTrend, int Current, int Previous, int Delta, DateTime? SinceUtc)
{
    /// <summary>The neutral "not enough history yet" value — shown until a second measure exists.</summary>
    public static readonly ScoreProgress None = new(false, 0, 0, 0, null);

    public bool IsImprovement => Delta > 0;
    public bool IsRegression => Delta < 0;

    /// <summary>Signed delta for display ("+13" / "-5" / "0") — the sign IS the honest direction marker.</summary>
    public string DeltaLabel => Delta > 0 ? $"+{Delta}" : Delta.ToString();

    /// <summary>French direction as a STATE, faithful to the sign: up, down, or genuinely flat. Deliberately
    /// "en hausse/baisse" (a state) rather than "en progression/recul" (a process) — with the dedupe timeline the
    /// last delta can be old, so the wording must stay true even after the score has been flat for weeks; the
    /// paired "depuis le …" date carries the when.</summary>
    public string DirectionLabel => Delta > 0 ? "En hausse" : Delta < 0 ? "En baisse" : "Score stable";

    /// <summary>Local date of the reading we're comparing against (display only — the stored instant is UTC).</summary>
    public string SinceLabel => SinceUtc?.ToLocalTime().ToString("dd/MM/yyyy") ?? string.Empty;

    /// <summary>The one-line French trend headline, the SINGLE source shared by the dashboard ring and the system
    /// report so the two surfaces can't word the same movement differently (the <see cref="TweakCategoryLabels"/>
    /// anti-drift pattern). A signed delta with its anchor date, or honest "stable" when flat. Empty when there's
    /// no trend yet (a first-ever measure), so a caller can bind/append it without a separate guard.</summary>
    public string TrendLine => !HasTrend
        ? string.Empty
        : Delta == 0
            ? $"{DirectionLabel} depuis le {SinceLabel}"
            : $"{DirectionLabel} · {DeltaLabel} pts depuis le {SinceLabel}";
}

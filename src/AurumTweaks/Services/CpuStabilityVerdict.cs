using AurumTweaks.Models;

namespace AurumTweaks.Services;

/// <summary>
/// Pure builder for the CPU stability page's verdict SENTENCE — the human-readable line shown on the page status and
/// pasted into the shareable report. Extracted so a SINGLE source decides the wording, with no drift between the
/// on-screen status and the report (mirrors how <see cref="LatencyVerdict"/> centralises its label). The outcome
/// itself is the shared <see cref="StabilityVerdict.Classify"/>, so « STABLE » NEVER appears unless the run completed
/// with zero errors, and a caught miscalculation outranks a cancellation. The « sur ce test » qualifier and the « ne
/// prouve rien » hedge are part of the sentence on purpose: a quick coherence test must never read as hours of
/// Prime95/OCCT, and the report carries that same honesty into the paste.
/// </summary>
public static class CpuStabilityVerdict
{
    public static string Describe(CpuTestResult r)
    {
        string kernel = r.Avx2Used ? "AVX2" : "scalaire";
        return StabilityVerdict.Classify(r.Completed, r.Cancelled, r.ErrorCount) switch
        {
            StabilityOutcome.Unstable =>
                $"INSTABLE — {r.ErrorCount} erreur(s) de calcul en {r.DurationSec:0}s (charge {kernel}). Monte le vcore ou réduis l'OC, puis relance.",
            StabilityOutcome.Cancelled =>
                $"Interrompu — {r.DurationSec:0}s testées ({kernel}), aucune erreur jusqu'ici. Ne prouve rien.",
            StabilityOutcome.Stable =>
                $"STABLE sur ce test — {r.DurationSec:0}s · {r.ThreadsUsed} threads · charge {kernel} · 0 erreur de calcul.",
            _ => r.Notes.Count > 0 ? r.Notes[0] : "Le test n'a pas pu s'exécuter.",
        };
    }
}

using AurumTweaks.Models;

namespace AurumTweaks.Services;

/// <summary>
/// Pure builder for the RAM stability page's verdict SENTENCE — the human-readable line shown on the page status and
/// pasted into the shareable report. Extracted so a SINGLE source decides the wording, with no drift between the
/// on-screen status and the report (mirrors <see cref="CpuStabilityVerdict"/> on the CPU side). The outcome itself is
/// the shared <see cref="StabilityVerdict.Classify"/>, so « STABLE » NEVER appears unless the run completed with zero
/// errors, and a caught bit-flip outranks a cancellation. The « sur ce test » qualifier and the « ne prouve rien »
/// hedge are part of the sentence on purpose: a quick coverage test must never read as hours of TM5/Karhu/OCCT, and
/// the report carries that same honesty into the paste.
/// </summary>
public static class MemoryStabilityVerdict
{
    public static string Describe(MemoryTestResult r) =>
        StabilityVerdict.Classify(r.Completed, r.Cancelled, r.ErrorCount) switch
        {
            StabilityOutcome.Unstable =>
                $"INSTABLE — {r.ErrorCount} erreur(s) sur {r.SizeMbTested} Mo. Desserre les timings (ou monte la VDIMM/VSOC) et relance.",
            StabilityOutcome.Cancelled =>
                $"Interrompu — {r.SizeMbTested} Mo partiellement testés, aucune erreur jusqu'ici. Ne prouve rien.",
            StabilityOutcome.Stable =>
                $"STABLE sur ce test — {r.SizeMbTested} Mo · {r.PassesCompleted} passe(s) · {r.AvgThroughputMbps / 1000d:0.0} Go/s, 0 erreur.",
            _ => r.Notes.Count > 0 ? r.Notes[0] : "Le test n'a pas pu s'exécuter.",
        };
}

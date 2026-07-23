using System;

namespace AurumTweaks.Services;

/// <summary>
/// Pure decision logic for the post-apply <b>auto-revert safety net</b> (the "Conserver ?" prompt, like
/// Windows display-settings). A bad core/memory offset — or an AMD GPU/VRAM frequency push — can
/// black-screen or hard-hang the GPU, and the user may not be able to reach the Reset button. So after
/// such an apply, Aurum holds the PREVIOUS profile and re-applies it automatically unless the user
/// confirms within a short window. This models only the WHEN (which changes are risky enough to warrant
/// the net) and the countdown arithmetic/label; the DispatcherTimer + the real re-apply live in the VM.
/// Side-effect-free → unit-testable.
/// </summary>
public static class GpuOcAutoRevert
{
    /// <summary>Default confirmation window (seconds) before the previous profile is re-applied.</summary>
    public const int DefaultWindowSeconds = 15;

    /// <summary>
    /// True when going from <paramref name="before"/> to <paramref name="after"/> changed an axis that
    /// can plausibly crash or black-screen the GPU — the core/memory clock offsets (NVIDIA) or the AMD
    /// GPU/VRAM max frequency. Power-limit and temperature-target changes are deliberately NOT counted:
    /// they are bounded by the card's own window and can't hang the display, so forcing a countdown on
    /// them would be noise. Voltage is never applied, so it never counts.
    /// </summary>
    public static bool WarrantsCountdown(GpuOcProfile before, GpuOcProfile after) =>
        before.CoreOffsetMhz != after.CoreOffsetMhz
        || before.MemoryOffsetMhz != after.MemoryOffsetMhz
        || before.AmdMaxFreqMhz != after.AmdMaxFreqMhz
        || before.AmdMaxVramFreqMhz != after.AmdMaxVramFreqMhz;
}

/// <summary>
/// Immutable countdown state for the auto-revert prompt. Ticks once per second toward zero; at zero the
/// VM re-applies the captured previous profile. A separate type from the decision above so the VM can
/// bind its <see cref="Label"/> and drive a timer against a value it can also unit-test.
/// </summary>
public readonly record struct OcAutoRevertCountdown(int TotalSeconds, int RemainingSeconds)
{
    /// <summary>Begin a countdown of <paramref name="seconds"/> (clamped to at least 1).</summary>
    public static OcAutoRevertCountdown Start(int seconds = GpuOcAutoRevert.DefaultWindowSeconds)
    {
        int s = Math.Max(1, seconds);
        return new OcAutoRevertCountdown(s, s);
    }

    /// <summary>One second elapsed — remaining never goes below zero.</summary>
    public OcAutoRevertCountdown Tick() => this with { RemainingSeconds = Math.Max(0, RemainingSeconds - 1) };

    /// <summary>The window has elapsed → the VM must re-apply the previous profile now.</summary>
    public bool Expired => RemainingSeconds <= 0;

    /// <summary>User-facing prompt; asks to keep, and states the automatic outcome so nothing is hidden.</summary>
    public string Label => Expired
        ? "Retour aux réglages précédents…"
        : $"Conserver ces réglages ? Retour automatique dans {RemainingSeconds} s.";
}

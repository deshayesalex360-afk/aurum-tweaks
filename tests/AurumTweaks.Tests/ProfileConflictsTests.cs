using System;
using System.Collections.Generic;
using AurumTweaks.Models;
using AurumTweaks.Services;
using Xunit;

namespace AurumTweaks.Tests;

/// <summary>
/// Pins <see cref="ProfileConflicts.Summarize"/> — the one honest line a profile card shows when its resolved set is
/// internally contradictory (two+ tweaks writing the same target to different values, where apply order silently
/// decides the winner). The load-bearing rule: a consistent set claims NOTHING (empty string → the card hides the
/// line), so the warning can never be a phantom. The detector's findings drive the count; this only phrases them.
/// </summary>
public class ProfileConflictsTests
{
    private static TweakConflict C(int divergentValues = 2)
    {
        var values = new List<ConflictingValue>();
        for (var i = 0; i < divergentValues; i++)
            values.Add(new ConflictingValue(i.ToString(), new[] { "t" + i }));
        return new TweakConflict("Registre", @"HKLM\K\V", values);
    }

    [Fact]
    public void Summarize_NoConflicts_IsEmpty_NoPhantomWarning()
        => Assert.Equal(string.Empty, ProfileConflicts.Summarize(Array.Empty<TweakConflict>()));

    [Fact]
    public void Summarize_OneConflict_StatesTheCount_AndTheApplyOrderCaveat()
    {
        var text = ProfileConflicts.Summarize(new[] { C() });

        Assert.Contains("1 réglage(s) en conflit", text);
        Assert.Contains("l'ordre décide", text);   // the honest "why it matters": last write wins, invisibly
    }

    [Fact]
    public void Summarize_CountsEveryConflictingTarget()
    {
        var text = ProfileConflicts.Summarize(new[] { C(), C(3), C() });

        Assert.Contains("3 réglage(s) en conflit", text);   // three distinct targets, regardless of how many values each
    }
}

using AurumTweaks.Services;
using Xunit;

namespace AurumTweaks.Tests;

/// <summary>
/// Pins <see cref="NetworkQualityGrade"/> — the honest connection-stability verdict. The load-bearing property is
/// "worst component wins": a path that loses every packet must never grade "Excellent" just because its ping and
/// jitter round to zero (exactly the snapshot <see cref="NetworkRouteMath.Aggregate"/> emits on total loss).
/// Assertions stay culture-independent — enum values and label words, never the F1-formatted numbers (this scene's
/// machines run fr-FR, where the decimal separator is a comma).
/// </summary>
public class NetworkQualityGradeTests
{
    private static NetworkRouteSnapshot Snap(float ping, float jitter, float loss) => new(ping, jitter, loss, 0);

    // --- Component thresholds: hold two dimensions at "Excellent" and walk the third. ---

    [Theory]
    [InlineData(0f, NetworkQuality.Excellent)]
    [InlineData(1f, NetworkQuality.Good)]   // <= 1 %
    [InlineData(3f, NetworkQuality.Fair)]   // <= 5 %
    [InlineData(5f, NetworkQuality.Fair)]
    [InlineData(6f, NetworkQuality.Poor)]
    [InlineData(100f, NetworkQuality.Poor)]
    public void PacketLoss_DrivesGrade_WhenWorst(float loss, NetworkQuality expected) =>
        Assert.Equal(expected, NetworkQualityGrade.Assess(Snap(ping: 5f, jitter: 0.5f, loss: loss)).Quality);

    [Theory]
    [InlineData(0.5f, NetworkQuality.Excellent)] // < 2 ms
    [InlineData(2f, NetworkQuality.Good)]        // < 8 ms
    [InlineData(7f, NetworkQuality.Good)]
    [InlineData(8f, NetworkQuality.Fair)]        // < 16 ms
    [InlineData(15f, NetworkQuality.Fair)]
    [InlineData(16f, NetworkQuality.Poor)]
    [InlineData(40f, NetworkQuality.Poor)]
    public void Jitter_DrivesGrade_WhenWorst(float jitter, NetworkQuality expected) =>
        Assert.Equal(expected, NetworkQualityGrade.Assess(Snap(ping: 5f, jitter: jitter, loss: 0f)).Quality);

    [Theory]
    [InlineData(10f, NetworkQuality.Excellent)] // < 20 ms
    [InlineData(20f, NetworkQuality.Good)]      // < 50 ms
    [InlineData(49f, NetworkQuality.Good)]
    [InlineData(50f, NetworkQuality.Fair)]      // < 100 ms
    [InlineData(99f, NetworkQuality.Fair)]
    [InlineData(100f, NetworkQuality.Poor)]
    [InlineData(250f, NetworkQuality.Poor)]
    public void Ping_DrivesGrade_WhenWorst(float ping, NetworkQuality expected) =>
        Assert.Equal(expected, NetworkQualityGrade.Assess(Snap(ping: ping, jitter: 0.5f, loss: 0f)).Quality);

    // --- Labels map to the French wording. ---

    [Theory]
    [InlineData(NetworkQuality.Excellent, "Excellent")]
    [InlineData(NetworkQuality.Good, "Bon")]
    [InlineData(NetworkQuality.Fair, "Moyen")]
    [InlineData(NetworkQuality.Poor, "Médiocre")]
    public void Label_MatchesQuality(NetworkQuality q, string expected)
    {
        // A snapshot whose only non-Excellent component is ping, landing exactly on q.
        var snap = q switch
        {
            NetworkQuality.Excellent => Snap(5f, 0.5f, 0f),
            NetworkQuality.Good => Snap(30f, 0.5f, 0f),
            NetworkQuality.Fair => Snap(70f, 0.5f, 0f),
            _ => Snap(200f, 0.5f, 0f),
        };
        var g = NetworkQualityGrade.Assess(snap);
        Assert.Equal(q, g.Quality);
        Assert.Equal(expected, g.Label);
    }

    // --- THE honesty test: total packet loss (ping/jitter round to 0) is never "Excellent". ---
    // This is exactly what NetworkRouteMath.Aggregate emits when every probe fails: (0, 0, 100, 0).

    [Fact]
    public void TotalPacketLoss_IsNeverExcellent_EvenWithZeroPingAndJitter()
    {
        var g = NetworkQualityGrade.Assess(new NetworkRouteSnapshot(0f, 0f, 100f, 0));
        Assert.Equal(NetworkQuality.Poor, g.Quality);
        Assert.Equal("Médiocre", g.Label);
        Assert.Contains("perte de paquets", g.Detail);
    }

    // --- Detail names the dominant limiting factor, in impact order (loss → jitter → ping). ---

    [Fact]
    public void Detail_NamesPacketLoss_WhenLossDominates() =>
        Assert.Contains("Limité par la perte de paquets",
            NetworkQualityGrade.Assess(Snap(ping: 5f, jitter: 0.5f, loss: 10f)).Detail);

    [Fact]
    public void Detail_NamesJitter_WhenJitterDominates() =>
        Assert.Contains("Limité par la gigue",
            NetworkQualityGrade.Assess(Snap(ping: 5f, jitter: 20f, loss: 0f)).Detail);

    [Fact]
    public void Detail_NamesLatency_WhenPingDominates() =>
        Assert.Contains("Limité par la latence",
            NetworkQualityGrade.Assess(Snap(ping: 200f, jitter: 0.5f, loss: 0f)).Detail);

    // --- Excellent path lists all three metrics rather than a limiting factor. ---

    [Fact]
    public void Detail_OnExcellentPath_ListsMetrics_NotALimitingFactor()
    {
        var g = NetworkQualityGrade.Assess(Snap(ping: 8f, jitter: 0.5f, loss: 0f));
        Assert.Equal(NetworkQuality.Excellent, g.Quality);
        Assert.Contains("Aucune perte", g.Detail);
        Assert.DoesNotContain("Limité par", g.Detail);
    }

    // --- The indicative caveat is never dropped, on any grade. ---

    [Theory]
    [InlineData(8f, 0.5f, 0f)]   // Excellent
    [InlineData(30f, 0.5f, 0f)]  // Good
    [InlineData(70f, 0.5f, 0f)]  // Fair
    [InlineData(0f, 0f, 100f)]   // Poor (total loss)
    public void Detail_AlwaysCarriesTheIndicativeCaveat(float ping, float jitter, float loss) =>
        Assert.Contains("indicatif : mesuré vers la cible, pas le serveur de jeu",
            NetworkQualityGrade.Assess(Snap(ping, jitter, loss)).Detail);
}

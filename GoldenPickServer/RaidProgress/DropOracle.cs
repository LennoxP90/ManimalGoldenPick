namespace GoldenPick.RaidProgress;

// the crate drop decision, ported verbatim from the relay's /raid/end handler. pure logic +
// an injected RNG so it's deterministic under test: a cycle-boundary raid rolls nextRandom()
// and awards when the roll lands under DropProbability.
public static class DropOracle
{
    public const int RaidCycleSize = 5;        // roll once every Nth survived raid
    public const double DropProbability = 0.0051; // 0.51%

    // survivedCount is the post-increment total. returns true only on a cycle boundary AND a
    // winning roll. nextRandom must yield [0,1); pass () => Random.Shared.NextDouble() in prod.
    public static bool ShouldAward(int survivedCount, Func<double> nextRandom)
    {
        if (survivedCount <= 0 || survivedCount % RaidCycleSize != 0) return false;
        return nextRandom() < DropProbability;
    }
}

using System;

namespace MitPlanner.Decision;

/// <summary>
/// "Use this cooldown now, or keep it for what's next?" scored in HP: damage it prevents now vs
/// damage it would prevent at the next pull (or boss) if it's still recasting then. The same
/// formulas are mirrored in tools/replay_trash.py; change both together.
///
///   now  = incoming %/s x min(duration, rest of this pull) x reduction x Danger(HP now)
///   held = 0 if it's back before the next pull starts; else
///          expected %/s there x min(duration, next pull length) x reduction
///          x the share of the next pull it would miss
///   (No HP weighting on "held": HP at the next pull is unknown, and weighting it made every pull lose
///   to the next one, so the big mit would never go out. Replay of 2026-11-03..10: 28 of 37 held.
///   Without it, holding only wins when what's next is clearly worse: a boss or a bigger pack.)
///   expected %/s at the next pull: this pull's incoming per mob x its mobs (packs), or BossRate
///          (bosses; their tank busters are what the big mits are for), blended by the boss chance
///   hold only if held > now x HoldBias (what's next is clearly worse); always use when survival is at risk (see <see cref="Critical"/>).
/// [Assumption] the weights (Danger curve, BossRate, HoldBias, survival thresholds) are first
/// guesses, set so the Hullbreaker death would have been prevented and an ordinary pull isn't
/// changed; tune them from the DECIDE lines in the trash logs.
/// </summary>
public static class MitScorer
{
    /// <summary>Incoming %/s assumed at a boss for a big mit (busters are spiky; this averages one over its window).</summary>
    public const float BossRate = 4f;
    /// <summary>Hold only when the held value beats the value now by this factor (favours using it: a mit in hand is real).</summary>
    public const float HoldBias = 1.25f;
    /// <summary>Below this HP, or when this many seconds of current damage would take you under 20%, always use.</summary>
    public const float CriticalHp = 45f;
    public const float CriticalSeconds = 4f;

    /// <summary>Weight for HP: 1 at 70%+, rising to 3 at 30% and below. Damage taken low matters more.</summary>
    public static float Danger(float hpPct) => 1f + 2f * Math.Clamp((70f - hpPct) / 40f, 0f, 1f);

    public static bool Critical(float hpPct, float netPctPerSec) =>
        hpPct < CriticalHp || hpPct - Math.Max(0f, netPctPerSec) * CriticalSeconds < 20f;

    public readonly record struct Score(float Now, float Held, bool Critical, bool Use, string Why)
    {
        public override string ToString() => $"now={Now:0.0} held={Held:0.0}{(Critical ? " critical" : "")} -> {(Use ? "use" : "hold")} ({Why})";
    }

    /// <param name="hpPct">Your HP %.</param>
    /// <param name="incomingPctPerSec">Damage taken now, % of max HP per second (before heals).</param>
    /// <param name="netPctPerSec">Damage minus healing, %/s (positive = losing HP).</param>
    /// <param name="mobs">Mobs on you now.</param>
    /// <param name="restOfPull">Seconds this pull is expected to last.</param>
    /// <param name="reduction">0.4 for a 40% mit.</param>
    /// <param name="duration">Seconds it lasts.</param>
    /// <param name="recast">Its cooldown, seconds.</param>
    public static Score Evaluate(float hpPct, float incomingPctPerSec, float netPctPerSec, int mobs, float restOfPull,
        float reduction, float duration, float recast, Lookahead next)
    {
        var now = incomingPctPerSec * Math.Min(duration, Math.Max(0f, restOfPull)) * reduction * Danger(hpPct);
        var critical = Critical(hpPct, netPctPerSec);

        var held = 0f;
        if (recast > next.SecondsUntil && !float.IsInfinity(next.SecondsUntil))
        {
            var perMob = mobs > 0 ? incomingPctPerSec / mobs : incomingPctPerSec;
            var packRate = perMob * Math.Max(1f, next.NextMobs);
            var rate = next.BossChance * BossRate + (1 - next.BossChance) * packRate;
            var window = Math.Min(duration, Math.Max(1f, next.NextDuration));
            // Still recasting for this much of the next pull (all of it if it isn't back by the end).
            var missed = Math.Clamp((recast - next.SecondsUntil) / Math.Max(1f, next.NextDuration), 0f, 1f);
            held = rate * window * reduction * missed;
        }

        if (critical) return new(now, held, true, true, "survival");
        if (held <= 0) return new(now, held, false, true, "back before the next pull");
        var use = held <= now * HoldBias;
        return new(now, held, false, use, use ? "worth more now" : $"keep for the {(next.NextIsBoss ? "boss" : "next pack")}");
    }
}

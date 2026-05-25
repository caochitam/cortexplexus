using CortexPlexus.Core.Models;

namespace CortexPlexus.Memory;

/// <summary>
/// Pure scoring functions for agent memory decay. The <see cref="Score"/> method
/// mirrors the SQL expression in <see cref="ScoreSqlExpression"/>; both encode the
/// Weibull decay defined in ADR-012.
/// </summary>
public static class MemoryScoring
{
    /// <summary>Weibull shape parameter. See ADR-012 for why 1.5.</summary>
    public const double Shape = 1.5;

    /// <summary>Auto-forget threshold. Memories below this score are reaped.</summary>
    public const double ForgetThreshold = 0.1;

    /// <summary>
    /// Decay scale (days) for <c>session</c>-scoped memories — overrides the per-topic scale.
    /// Session memories are transient working state; a short scale means an untouched session
    /// memory (importance 0.5) drops below <see cref="ForgetThreshold"/> in ~1.4 days and gets
    /// reaped, while active use keeps refreshing last_accessed_at. Mirrored in
    /// <see cref="ScoreSqlExpression"/> — change both together.
    /// </summary>
    public const double SessionScaleDays = 1.0;

    /// <summary>
    /// Effective decay scale (days) for a memory: <see cref="SessionScaleDays"/> when the scope is
    /// session, else the per-topic scale. Keeps the session override in one place.
    /// </summary>
    public static double ScaleDaysFor(string? scope, string? topic) =>
        scope == MemoryScope.Session ? SessionScaleDays : ScaleDaysForTopic(topic);

    /// <summary>Per-topic scale (days). Null topic falls back to the <see cref="Note"/> default.</summary>
    public static double ScaleDaysForTopic(string? topic) => topic switch
    {
        MemoryTopic.Preference => 365,
        MemoryTopic.Pattern    => 180,
        MemoryTopic.Decision   => 180,
        MemoryTopic.Bug        =>  90,
        MemoryTopic.Todo       =>  30,
        MemoryTopic.Note       =>  60,
        _                      =>  60,
    };

    /// <summary>
    /// Compute the current decay-weighted score.
    /// </summary>
    public static double Score(AgentMemory memory, DateTimeOffset now)
    {
        var lambdaDays = ScaleDaysFor(memory.Scope, memory.Topic);
        var tDays = (now - memory.LastAccessedAt).TotalDays;
        if (tDays < 0) tDays = 0; // Clock drift guard — treat future timestamps as "just now".
        var decay = Math.Exp(-Math.Pow(tDays / lambdaDays, Shape));
        return memory.Importance * decay;
    }

    /// <summary>
    /// SQL expression producing the same score as <see cref="Score"/>. Callers splice
    /// this directly into <c>ORDER BY</c> and <c>WHERE</c> clauses. No parameters needed —
    /// all constants are inlined and the column names are fixed.
    /// </summary>
    public const string ScoreSqlExpression = """
        importance * exp(
            -power(
                greatest(0, extract(epoch from (now() - last_accessed_at))) / (86400.0 * CASE
                    WHEN scope = 'session' THEN 1
                    WHEN topic = 'preference' THEN 365
                    WHEN topic = 'pattern'    THEN 180
                    WHEN topic = 'decision'   THEN 180
                    WHEN topic = 'bug'        THEN  90
                    WHEN topic = 'todo'       THEN  30
                    WHEN topic = 'note'       THEN  60
                    ELSE                            60
                END),
                1.5
            )
        )
        """;
}

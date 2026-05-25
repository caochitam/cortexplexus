using CortexPlexus.Core.Models;

namespace CortexPlexus.Memory.Tests;

/// <summary>Pure unit tests — no DB. Locks in the decay formula against future drift.</summary>
public sealed class MemoryScoringTests
{
    private static AgentMemory Mem(
        double importance, string? topic, DateTimeOffset lastAccessed) =>
        new(
            Id: Guid.NewGuid(),
            Content: "x",
            Scope: MemoryScope.Global,
            ScopeId: null,
            Topic: topic,
            Importance: importance,
            RelatedFqns: Array.Empty<string>(),
            CreatedAt: lastAccessed,
            LastAccessedAt: lastAccessed,
            AccessCount: 0);

    [Fact]
    public void Score_JustAccessed_EqualsImportance()
    {
        var now = DateTimeOffset.UtcNow;
        var mem = Mem(0.7, MemoryTopic.Preference, now);
        var s = MemoryScoring.Score(mem, now);
        Assert.Equal(0.7, s, precision: 4);
    }

    [Theory]
    [InlineData(MemoryTopic.Preference, 365)]
    [InlineData(MemoryTopic.Pattern, 180)]
    [InlineData(MemoryTopic.Decision, 180)]
    [InlineData(MemoryTopic.Bug, 90)]
    [InlineData(MemoryTopic.Todo, 30)]
    [InlineData(MemoryTopic.Note, 60)]
    [InlineData(null, 60)]
    [InlineData("unknown-topic", 60)]
    public void ScaleDaysForTopic_ReturnsExpected(string? topic, double expected)
    {
        Assert.Equal(expected, MemoryScoring.ScaleDaysForTopic(topic));
    }

    [Fact]
    public void Score_TodoAfter45Days_BelowForgetThreshold()
    {
        var now = DateTimeOffset.UtcNow;
        var mem = Mem(0.5, MemoryTopic.Todo, now.AddDays(-45));
        var s = MemoryScoring.Score(mem, now);
        Assert.True(s < MemoryScoring.ForgetThreshold,
            $"Expected score<{MemoryScoring.ForgetThreshold} for 45-day-old todo, got {s:F4}");
    }

    [Fact]
    public void Score_PreferenceAfter365Days_StillAboveForget()
    {
        var now = DateTimeOffset.UtcNow;
        var mem = Mem(0.8, MemoryTopic.Preference, now.AddDays(-365));
        var s = MemoryScoring.Score(mem, now);
        Assert.True(s >= MemoryScoring.ForgetThreshold,
            $"Expected score>={MemoryScoring.ForgetThreshold} for 365-day-old preference, got {s:F4}");
    }

    [Fact]
    public void ScaleDaysFor_SessionScope_OverridesTopic()
    {
        // session scope forces the short scale regardless of topic; other scopes use topic scale.
        Assert.Equal(MemoryScoring.SessionScaleDays,
            MemoryScoring.ScaleDaysFor(MemoryScope.Session, MemoryTopic.Preference));
        Assert.Equal(365, MemoryScoring.ScaleDaysFor(MemoryScope.Project, MemoryTopic.Preference));
        Assert.Equal(365, MemoryScoring.ScaleDaysFor(MemoryScope.Global, MemoryTopic.Preference));
    }

    [Fact]
    public void Score_SessionScope_DecaysFastRegardlessOfTopic()
    {
        var now = DateTimeOffset.UtcNow;
        // A 'note' (60-day topic scale) — but session override makes λ ~1 day. After 2 days
        // untouched, a session memory is forgotten while an identical project memory survives.
        var session = Mem(0.5, MemoryTopic.Note, now.AddDays(-2)) with { Scope = MemoryScope.Session };
        var project = Mem(0.5, MemoryTopic.Note, now.AddDays(-2)) with { Scope = MemoryScope.Project };

        var sSession = MemoryScoring.Score(session, now);
        var sProject = MemoryScoring.Score(project, now);

        Assert.True(sSession < MemoryScoring.ForgetThreshold,
            $"Expected session note forgotten after 2 days, got {sSession:F4}");
        Assert.True(sProject >= MemoryScoring.ForgetThreshold,
            $"Expected project note to survive 2 days, got {sProject:F4}");
    }

    [Fact]
    public void Score_MonotonicallyDecreasesInT()
    {
        var now = DateTimeOffset.UtcNow;
        double prev = double.PositiveInfinity;
        for (var d = 0; d <= 200; d += 10)
        {
            var mem = Mem(0.5, MemoryTopic.Note, now.AddDays(-d));
            var s = MemoryScoring.Score(mem, now);
            Assert.True(s <= prev + 1e-9, $"Score not monotonic at d={d}: prev={prev:F4} now={s:F4}");
            prev = s;
        }
    }

    [Fact]
    public void Score_FutureTimestamp_ClampedToImportance()
    {
        var now = DateTimeOffset.UtcNow;
        var mem = Mem(0.6, MemoryTopic.Note, now.AddDays(5)); // clock drift
        var s = MemoryScoring.Score(mem, now);
        Assert.Equal(0.6, s, precision: 4);
    }

    [Fact]
    public void Score_ImportanceZero_AlwaysZero()
    {
        var now = DateTimeOffset.UtcNow;
        var mem = Mem(0.0, MemoryTopic.Preference, now);
        Assert.Equal(0.0, MemoryScoring.Score(mem, now));
        var older = Mem(0.0, MemoryTopic.Preference, now.AddDays(-100));
        Assert.Equal(0.0, MemoryScoring.Score(older, now));
    }
}

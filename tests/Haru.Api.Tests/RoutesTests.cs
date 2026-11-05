using Haru.Api.Contracts;
using Haru.Api.Workflow;

namespace Haru.Api.Tests;

public class RoutesTests
{
    private static PlanContext Ctx(bool? valid = true, int iteration = 1, bool onlyFixed = false, string decision = "") => new()
    {
        Day = "2026-11-05",
        WorkStart = "09:00",
        WorkEnd = "18:00",
        Iteration = iteration,
        OnlyFixedViolations = onlyFixed,
        Decision = decision,
        Report = valid is null
            ? null
            : new ValidationReport(valid.Value, 0.5, 240, 480, valid.Value ? [] : [new Violation("OVERLAP", "겹칩니다.", ["b1", "b2"])]),
    };

    /// <summary>The validator edges that accept the context.</summary>
    private static string[] ValidatorEdges(PlanContext? c) => new[]
        {
            (Name: "critic", Taken: Routes.ToCritic(c)),
            (Name: "scheduler", Taken: Routes.BackToScheduler(c)),
            (Name: "repair", Taken: Routes.ToRepair(c)),
        }
        .Where(e => e.Taken).Select(e => e.Name).ToArray();

    [Fact]
    public void ValidatorEdges_NullContext_TakeNoRoute() => Assert.Empty(ValidatorEdges(null));

    [Fact]
    public void CriticEdges_NullContext_OnlyAccept()
    {
        Assert.False(Routes.Revise(null));
        Assert.True(Routes.Accept(null));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void ValidatorEdges_ValidReport_GoToCritic(int iteration) =>
        Assert.Equal(new[] { "critic" }, ValidatorEdges(Ctx(valid: true, iteration: iteration)));

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void ValidatorEdges_OnlyFixedViolations_GoToCriticAtAnyIteration(int iteration) =>
        Assert.Equal(new[] { "critic" }, ValidatorEdges(Ctx(valid: false, iteration: iteration, onlyFixed: true)));

    [Fact]
    public void ValidatorEdges_InvalidBelowIterationLimit_GoBackToScheduler() =>
        Assert.Equal(new[] { "scheduler" }, ValidatorEdges(Ctx(valid: false, iteration: 1)));

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void ValidatorEdges_InvalidAtOrAboveIterationLimit_GoToRepair(int iteration) =>
        Assert.Equal(new[] { "repair" }, ValidatorEdges(Ctx(valid: false, iteration: iteration)));

    [Theory]
    [InlineData(true, false, 1)]
    [InlineData(true, false, 2)]
    [InlineData(true, true, 1)]
    [InlineData(false, false, 1)]
    [InlineData(false, false, 2)]
    [InlineData(false, false, 3)]
    [InlineData(false, true, 1)]
    [InlineData(false, true, 2)]
    public void ValidatorEdges_EveryValidatedContext_TakesExactlyOneRoute(bool valid, bool onlyFixed, int iteration) =>
        Assert.Single(ValidatorEdges(Ctx(valid, iteration, onlyFixed)));

    [Fact]
    public void CriticEdges_ReviseDecision_GoesBackToScheduler()
    {
        var c = Ctx(decision: "revise");

        Assert.True(Routes.Revise(c));
        Assert.False(Routes.Accept(c));
    }

    [Theory]
    [InlineData("approve")]
    [InlineData("")]
    [InlineData("anything else")]
    public void CriticEdges_AnyOtherDecision_GoesToCoach(string decision)
    {
        var c = Ctx(decision: decision);

        Assert.False(Routes.Revise(c));
        Assert.True(Routes.Accept(c));
    }
}

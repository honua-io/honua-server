// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.SpatialAnalytics.Domain;
using Honua.Geoprocessing.Execution;
using Honua.TestKit.Attributes;
using NetTopologySuite.Features;
using NetTopologySuite.Geometries;

namespace Honua.Server.Tests.Features.Geoprocessing.Execution;

/// <summary>
/// Bounding and cancellation coverage for the shared managed spatial join
/// (honua-server#3075). The carried-value budget only charges SUCCESSFUL matches, so it
/// cannot see the failure mode these tests pin down: inputs whose envelopes overlap
/// broadly but whose exact predicates mostly fail burn CPU proportional to
/// targets x dataset while charging almost nothing, and before the fix nothing observed
/// cancellation inside the candidate loop either.
/// </summary>
public sealed class SpatialJoinSupportBudgetTests
{
    private static readonly GeometryFactory Factory = new();

    [UnitTest]
    public void Join_CandidatesOverlapEnvelopeButFailPredicate_ChargesTheCandidateBudget()
    {
        // A diagonal line's envelope covers the whole square, so every off-diagonal point
        // survives the STRtree prune and reaches the exact predicate — and every one of them
        // fails it. Zero matches means zero carried values, so the carried budget stays
        // untouched no matter how many comparisons this costs.
        var (target, index) = BuildOffDiagonalCase(candidateCount: 200);
        var budget = new SpatialJoinSupport.MatchBudget(limit: 1_000_000, candidateLimit: 50);

        var act = () => SpatialJoinSupport.Join(
            target, index, SpatialJoinPredicate.Intersects, distance: 0, carryFields: [], stats: [], budget);

        act.Should().Throw<Exception>()
            .WithMessage("*cumulative candidate budget of 50*");
    }

    [UnitTest]
    public void Join_CandidateBudgetNotExhausted_Completes()
    {
        // The bound must not fire on an ordinary join: 200 comparisons against a ceiling of
        // 1000 completes and reports its (zero) matches normally.
        var (target, index) = BuildOffDiagonalCase(candidateCount: 200);
        var budget = new SpatialJoinSupport.MatchBudget(limit: 1_000_000, candidateLimit: 1_000);

        var result = SpatialJoinSupport.Join(
            target, index, SpatialJoinPredicate.Intersects, distance: 0, carryFields: [], stats: [], budget);

        result.Attributes.GetOptionalValue(SpatialJoinSupport.JoinCountAttribute).Should().Be(0L);
    }

    [UnitTest]
    public void Join_CarriesNoFields_IsStillBounded()
    {
        // The case the carried-value budget structurally cannot bound. Charge(0) returns
        // immediately, so before the candidate budget existed a join carrying no fields had
        // NO ceiling at all — it ran to completion however long that took.
        var (target, index) = BuildOffDiagonalCase(candidateCount: 200);
        var budget = new SpatialJoinSupport.MatchBudget(limit: 1_000_000, candidateLimit: 25);

        var act = () => SpatialJoinSupport.Join(
            target, index, SpatialJoinPredicate.Intersects, distance: 0, carryFields: [], stats: [], budget);

        act.Should().Throw<Exception>().WithMessage("*candidate budget*");
    }

    [UnitTest]
    public void Join_LoweredCarriedValueBudget_DoesNotLowerTheCandidateCeiling()
    {
        // `maxCarriedMatchValues` is a caller-facing knob that may only be LOWERED, and the
        // carried-value budget is legitimately zero for a join that carries no fields. Deriving
        // the candidate ceiling from it therefore turned an intentional, modest carried-value
        // cap into a candidate cap that fires on the first comparison — failing joins that were
        // never pathological, and with the wrong remedy attached. The two ceilings count
        // different things and must stay independent.
        var (target, index) = BuildOffDiagonalCase(candidateCount: 200);
        var budget = new SpatialJoinSupport.MatchBudget(limit: 0);

        var result = SpatialJoinSupport.Join(
            target, index, SpatialJoinPredicate.Intersects, distance: 0, carryFields: [], stats: [], budget);

        result.Attributes.GetOptionalValue(SpatialJoinSupport.JoinCountAttribute).Should().Be(0L);
    }

    [UnitTest]
    public void Join_CancelledMidCandidateLoop_Throws()
    {
        // A single target against a broadly overlapping dataset can sit in this loop for a
        // long time. Checking the token only BETWEEN targets left a dismissed job holding a
        // worker until the current target finished.
        var (target, index) = BuildOffDiagonalCase(candidateCount: 200);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = () => SpatialJoinSupport.Join(
            target, index, SpatialJoinPredicate.Intersects, distance: 0, carryFields: [], stats: [],
            budget: null, cts.Token);

        act.Should().Throw<OperationCanceledException>();
    }

    /// <summary>
    /// Builds a target whose envelope contains every candidate but which intersects none of
    /// them: a diagonal line from (0,0) to (n,n) against points offset off that diagonal.
    /// </summary>
    private static (IFeature Target, NetTopologySuite.Index.Strtree.STRtree<IFeature> Index) BuildOffDiagonalCase(
        int candidateCount)
    {
        var target = new Feature(
            Factory.CreateLineString([new Coordinate(0, 0), new Coordinate(candidateCount, candidateCount)]),
            new AttributesTable());

        var joinFeatures = new List<IFeature>(candidateCount);
        for (var i = 0; i < candidateCount; i++)
        {
            // Off the diagonal by a half unit, so inside the target's envelope but never on it.
            joinFeatures.Add(new Feature(
                Factory.CreatePoint(new Coordinate(i, i + 0.5)),
                new AttributesTable()));
        }

        return (target, SpatialJoinSupport.BuildIndex(joinFeatures, CancellationToken.None));
    }
}

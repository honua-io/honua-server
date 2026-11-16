// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Architecture.Tests.GeoServicesParity;
using Xunit;

namespace Honua.Architecture.Tests;

[Trait("Category", "Architecture")]
public sealed class GeoServicesParityMatrixDriftTests
{
    private static readonly Lazy<GeoServicesParityJoin> SharedJoin =
        new(GeoServicesParityGenerator.Join, isThreadSafe: true);

    [ArchitectureTest]
    public void EveryServedOperation_CarriesAJudgment()
    {
        SharedJoin.Value.ServedButUnclassified.Should().BeEmpty(
            "every served GeoServices operation must carry a parity judgement in "
            + $"{GeoServicesParityGenerator.JudgmentRelativePath}. An unclassified served operation means we "
            + "shipped something the published matrix silently omits, while that matrix claims to be an "
            + "operation-level statement of coverage. Classify it honestly — implemented, partial, or stub — "
            + "and regenerate with scripts/ci/generate-geoservices-parity.sh. Do NOT relax this assertion, and do "
            + "NOT pick a status because it is convenient.");
    }

    [ArchitectureTest]
    public void EveryJudgment_ResolvesToAServedOperation()
    {
        SharedJoin.Value.ClassifiedButNotServed.Should().BeEmpty(
            "every operation classified implemented/partial/stub must resolve to a route Honua actually serves. "
            + "A judgement that resolves to nothing is an over-claim: a prospect who tests it gets a 404, which "
            + "costs more trust than the gap it was hiding. Remove the entry (or serve the route) — do not delete "
            + "the assertion.");
    }

    [ArchitectureTest]
    public void EveryNotImplementedOperation_IsAbsentFromTheServedSurface()
    {
        SharedJoin.Value.NotImplementedButServed.Should().BeEmpty(
            "an operation recorded as not-implemented must not actually be served. This is the under-claim "
            + "direction: shipped work still published as deferred. Promote the entry to its honest status "
            + "instead — which means classifying what it really does, not assuming 'implemented'.");
    }

    [ArchitectureTest]
    public void NoOperation_CarriesTwoJudgments()
    {
        SharedJoin.Value.ClassifiedTwice.Should().BeEmpty(
            "each served operation must be claimed by exactly one judgement entry; two entries can (and did) "
            + "disagree about the same route's status, so a reader cannot tell which claim is the real one.");
    }

    [ArchitectureTest]
    public void EveryJudgment_IsFiledUnderTheServingServiceType()
    {
        SharedJoin.Value.MisfiledUnderWrongService.Should().BeEmpty(
            "an operation must be classified under the matrix service that owns the Esri service type serving "
            + "it, or the service-level implementedSurface/knownGaps prose describes a different surface than "
            + "the operations filed beneath it.");
    }

    [ArchitectureTest]
    public void EveryServedServiceType_HasAMatrixHome()
    {
        SharedJoin.Value.ServiceTypesWithNoMatrixHome.Should().BeEmpty(
            "a served Esri service type must map to a service in the parity matrix (or be an explicit, "
            + "reasoned exclusion in GeoServicesRouteRoster.ExcludedServiceTypes). Silently omitting a whole "
            + "served service type is how VectorTileServer and VersionManagementServer stayed unmentioned.");
    }

    [ArchitectureTest]
    public void EveryGeoServicesRoute_Normalizes()
    {
        SharedJoin.Value.Roster.Unmapped.Should().BeEmpty(
            "every served route under a GeoServices route family must normalize to an Esri-relative operation "
            + "path. A route the normalization cannot place would otherwise be dropped from the roster, and a "
            + "dropped route is an operation the join can never require a judgement for.");
    }

    [ArchitectureTest]
    public void EveryRosterExclusion_StillMatchesAServedServiceType()
    {
        SharedJoin.Value.StaleExclusions.Should().BeEmpty(
            "each declared roster exclusion must still match a served service type. An exclusion matching "
            + "nothing is either stale or misspelled, and a misspelled exclusion excludes nothing while reading "
            + "as though it does.");
    }

    [ArchitectureTest]
    public void CommittedMatrix_EqualsFreshlyGeneratedOutput()
    {
        var committedPath = GeoServicesParityGenerator.CommittedMatrixPath();
        File.Exists(committedPath).Should().BeTrue(
            $"the published parity matrix must exist at {GeoServicesParityGenerator.MatrixRelativePath}");

        var committed = File.ReadAllText(committedPath).ReplaceLineEndings("\n");
        var generated = GeoServicesParityGenerator.Serialize(GeoServicesParityGenerator.Generate());

        committed.Should().Be(generated,
            $"{GeoServicesParityGenerator.MatrixRelativePath} is generated, never authored. Edit the judgement "
            + $"source ({GeoServicesParityGenerator.JudgmentRelativePath}) and run "
            + "scripts/ci/generate-geoservices-parity.sh, then commit the result.");
    }

    [ArchitectureTest]
    public void EveryJudgment_UsesTheDeclaredStatusVocabulary()
    {
        var judgment = SharedJoin.Value.Judgment;
        var offenders = judgment.ServiceList
            .SelectMany(service => service.OperationList.Select(operation => (service.Id, operation.Name, operation.Status)))
            .Where(entry => !GeoServicesParityGenerator.ServedStatuses.Contains(entry.Status, StringComparer.Ordinal))
            .Select(entry => $"{entry.Id}/{entry.Name}: '{entry.Status}'")
            .ToArray();

        offenders.Should().BeEmpty(
            "a served operation's status must be one of implemented/partial/stub. not_implemented is not a "
            + "status a served operation can carry — it belongs in absentOperations, which the join checks "
            + "against the served surface separately.");
    }

    [ArchitectureTest]
    public void EveryJudgment_BindsToAtLeastOneOperation()
    {
        var offenders = SharedJoin.Value.Judgment.ServiceList
            .SelectMany(service => service.OperationList.Select(operation => (service.Id, operation.Name, operation.PathList)))
            .Where(entry => entry.PathList.Length == 0)
            .Select(entry => $"{entry.Id}/{entry.Name}")
            .ToArray();

        offenders.Should().BeEmpty(
            "every judgement entry must name at least one geoServicesPath. An entry that binds to no operation is "
            + "invisible to the join in both directions: nothing proves it is true, and nothing notices when it "
            + "stops being true.");
    }

    [ArchitectureTest]
    public void EveryPartialOrStubJudgment_StatesItsGap()
    {
        var offenders = SharedJoin.Value.Judgment.ServiceList
            .SelectMany(service => service.OperationList.Select(operation => (service.Id, operation.Name, operation.Status, operation.Notes)))
            .Where(entry => entry.Status is "partial" or "stub" && string.IsNullOrWhiteSpace(entry.Notes))
            .Select(entry => $"{entry.Id}/{entry.Name} ({entry.Status})")
            .ToArray();

        offenders.Should().BeEmpty(
            "a partial or stub judgement must state what is missing. 'Partial' with no note tells a reader "
            + "nothing they can act on and hides the size of the gap.");
    }

    [ArchitectureTest]
    public void EveryEvidencePath_ResolvesToARealFile()
    {
        var root = ArchitectureTestHelpers.ResolveRepositoryRoot();

        var missing = SharedJoin.Value.Judgment.ServiceList
            .SelectMany(service => service.EvidenceMap
                .SelectMany(kind => kind.Value.Select(path => (service.Id, Kind: kind.Key, Path: path))))
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Path))
            .Where(entry => !File.Exists(ArchitectureTestHelpers.CombinePath(
                root, entry.Path.Replace('/', Path.DirectorySeparatorChar))))
            .Select(entry => $"{entry.Id}/{entry.Kind}: {entry.Path}")
            .ToArray();

        missing.Should().BeEmpty(
            "every evidence path cited by the parity matrix must resolve to a real file; a dead path means the "
            + "matrix is citing code that moved or was deleted, and a reader cannot verify the claim.");
    }
}

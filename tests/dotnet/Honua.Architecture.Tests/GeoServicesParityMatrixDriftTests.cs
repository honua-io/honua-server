// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Architecture.Tests.GeoServicesParity;
using Xunit;

namespace Honua.Architecture.Tests;

/// <summary>
/// The join gate for the published GeoServices REST parity matrix
/// (<c>docs/gis/data/geoservices-rest-parity.json</c>, #2861 / #2863). The matrix is
/// the most externally-consequential capability roster we publish — GitBook maps five
/// public URLs onto its <c>.md</c> summary — and it is the only ADR-0058 roster a
/// prospect reads.
/// </summary>
/// <remarks>
/// <para>
/// <b>Not to be confused with the import-fidelity scorecard.</b>
/// <c>import-fidelity-scorecard-governance.yml</c>, <c>geoservices-import-fidelity-nightly.yml</c>,
/// <c>GeoservicesImportFidelityIntegrationTests</c>, and <c>import-fidelity-scorecard-baseline.json</c>
/// measure whether <i>imported data</i> matches Esri's across ten dataset cases. They
/// never read this matrix and answer a different question. This file is the only gate
/// over the published matrix.
/// </para>
/// <para>
/// <b>What changed in #2863.</b> The interim guard (#2864) joined hand-listed matrix
/// claims against <c>EndpointRegistry.All</c> while the matrix kept its own parallel
/// hand roster — a second gate over a duplicated roster, which is precisely what
/// ADR-0058 says not to build. The roster is now <b>derived</b>
/// (<see cref="GeoServicesRouteRoster"/>) and the matrix <b>generated</b> from the join
/// of that roster with a hand-authored judgement source
/// (<see cref="GeoServicesParityGenerator"/>). So these tests no longer audit a copy;
/// they assert the join is total and that the published artifact is the join's output.
/// </para>
/// <para>
/// <b>What remains ungated, stated plainly:</b> whether a status is the <i>right</i>
/// status. Nothing here stops someone labelling a Stub as Implemented, or a Partial
/// whose parameter coverage has silently regressed. That needs human review — the
/// judgement source's <c>lastReviewed</c> date and the release checklist remain the
/// control. This gate makes the route-level claims honest, not the behavioural ones.
/// <b>It must never be used to justify upgrading a status to make a test pass.</b> If
/// a gate here creates that pressure, the gate is wrong: fix the gate, not the truth.
/// </para>
/// </remarks>
[Trait("Category", "Architecture")]
public sealed class GeoServicesParityMatrixDriftTests
{
    /// <summary>
    /// The join, computed once. It reads the committed judgement source and derives the
    /// roster from the generated capability data; both are pure, so sharing is safe and
    /// keeps the suite from re-deriving the roster per assertion.
    /// </summary>
    private static readonly Lazy<GeoServicesParityJoin> SharedJoin =
        new(GeoServicesParityGenerator.Join, isThreadSafe: true);

    /// <summary>
    /// Direction 1 — <b>served but unclassified</b>. We shipped a GeoServices operation
    /// and never said anything about it. This is the direction the interim guard could
    /// not enforce, and the one that let two whole served service types
    /// (VectorTileServer, VersionManagementServer) go unmentioned while the matrix
    /// presented itself as an operation-level coverage statement.
    /// </summary>
    [ArchitectureTest]
    public void EveryServedOperation_CarriesAJudgment()
    {
        SharedJoin.Value.ServedButUnclassified.Should().BeEmpty(
            "every served GeoServices operation must carry a parity judgement in "
            + $"{GeoServicesParityGenerator.JudgmentRelativePath}. An unclassified served operation means we "
            + "shipped something the published matrix silently omits, while that matrix claims to be an "
            + "operation-level statement of coverage. Classify it honestly — implemented, partial, or stub — "
            + "and regenerate with scripts/generate-geoservices-parity.sh. Do NOT relax this assertion, and do "
            + "NOT pick a status because it is convenient.");
    }

    /// <summary>
    /// Direction 2 — <b>classified but not served</b>. The matrix names an operation
    /// that resolves to no route. This is the over-claim a prospect discovers by
    /// getting a 404, and it is why this work exists: we published a <c>computeClass</c>
    /// route that never existed.
    /// </summary>
    [ArchitectureTest]
    public void EveryJudgment_ResolvesToAServedOperation()
    {
        SharedJoin.Value.ClassifiedButNotServed.Should().BeEmpty(
            "every operation classified implemented/partial/stub must resolve to a route Honua actually serves. "
            + "A judgement that resolves to nothing is an over-claim: a prospect who tests it gets a 404, which "
            + "costs more trust than the gap it was hiding. Remove the entry (or serve the route) — do not delete "
            + "the assertion.");
    }

    /// <summary>
    /// Direction 3 — <b>not-implemented but served</b>. Shipped work still published as
    /// deferred: the async exportTiles lifecycle (#2688 / #2831) that #2861 found stale
    /// within a day of landing.
    /// </summary>
    [ArchitectureTest]
    public void EveryNotImplementedOperation_IsAbsentFromTheServedSurface()
    {
        SharedJoin.Value.NotImplementedButServed.Should().BeEmpty(
            "an operation recorded as not-implemented must not actually be served. This is the under-claim "
            + "direction: shipped work still published as deferred. Promote the entry to its honest status "
            + "instead — which means classifying what it really does, not assuming 'implemented'.");
    }

    /// <summary>
    /// One operation, one judgement. Two entries claiming the same operation is how the
    /// hand matrix ended up publishing <c>computeHistograms</c> and <c>getSamples</c> as
    /// implemented <i>and</i> partial at the same time.
    /// </summary>
    [ArchitectureTest]
    public void NoOperation_CarriesTwoJudgments()
    {
        SharedJoin.Value.ClassifiedTwice.Should().BeEmpty(
            "each served operation must be claimed by exactly one judgement entry; two entries can (and did) "
            + "disagree about the same route's status, so a reader cannot tell which claim is the real one.");
    }

    /// <summary>
    /// A judgement must live under the service that actually serves the operation, so
    /// the per-service prose describes the routes it is filed against.
    /// </summary>
    [ArchitectureTest]
    public void EveryJudgment_IsFiledUnderTheServingServiceType()
    {
        SharedJoin.Value.MisfiledUnderWrongService.Should().BeEmpty(
            "an operation must be classified under the matrix service that owns the Esri service type serving "
            + "it, or the service-level implementedSurface/knownGaps prose describes a different surface than "
            + "the operations filed beneath it.");
    }

    /// <summary>
    /// A newly served Esri service type must be given a home in the matrix rather than
    /// silently omitted — the failure that hid VectorTileServer and
    /// VersionManagementServer.
    /// </summary>
    [ArchitectureTest]
    public void EveryServedServiceType_HasAMatrixHome()
    {
        SharedJoin.Value.ServiceTypesWithNoMatrixHome.Should().BeEmpty(
            "a served Esri service type must map to a service in the parity matrix (or be an explicit, "
            + "reasoned exclusion in GeoServicesRouteRoster.ExcludedServiceTypes). Silently omitting a whole "
            + "served service type is how VectorTileServer and VersionManagementServer stayed unmentioned.");
    }

    /// <summary>
    /// Every route under a GeoServices family must normalize, so the roster cannot
    /// under-report the served surface by quietly dropping what it fails to parse.
    /// </summary>
    [ArchitectureTest]
    public void EveryGeoServicesRoute_Normalizes()
    {
        SharedJoin.Value.Roster.Unmapped.Should().BeEmpty(
            "every served route under a GeoServices route family must normalize to an Esri-relative operation "
            + "path. A route the normalization cannot place would otherwise be dropped from the roster, and a "
            + "dropped route is an operation the join can never require a judgement for.");
    }

    /// <summary>
    /// A scope exclusion must still describe something real. An exclusion that matches
    /// nothing is either a typo silently widening the matrix's blind spot, or a stale
    /// note for a service type that no longer exists.
    /// </summary>
    [ArchitectureTest]
    public void EveryRosterExclusion_StillMatchesAServedServiceType()
    {
        SharedJoin.Value.StaleExclusions.Should().BeEmpty(
            "each declared roster exclusion must still match a served service type. An exclusion matching "
            + "nothing is either stale or misspelled, and a misspelled exclusion excludes nothing while reading "
            + "as though it does.");
    }

    /// <summary>
    /// The published artifact must equal freshly-generated output. This is what makes
    /// the derivation real rather than aspirational: a hand edit to a derived field
    /// (<c>esriPaths</c>, <c>honuaEndpoints</c>, <c>maturity</c>) cannot survive, and a
    /// route change that the judgement source has not caught up with fails here.
    /// Mirrors <c>FeatureCatalogDriftTests.CommittedCatalog_EqualsFreshlyGeneratedOutput</c>.
    /// </summary>
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
            + "scripts/generate-geoservices-parity.sh, then commit the result.");
    }

    /// <summary>
    /// Statuses must come from the declared vocabulary. <c>stub</c> in particular has an
    /// exact meaning — route exists, spec-shaped response, backing model deferred;
    /// read-stubs return empty/<c>false</c>, mutation-stubs return HTTP 400 rather than
    /// fabricating success — and an invented token would let that meaning drift.
    /// </summary>
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

    /// <summary>
    /// Every judgement must bind to at least one operation. An entry claiming nothing
    /// contributes no claims, so the join would neither reject it nor require it to be
    /// true — a status floating free of any route, which is how prose drifts back into
    /// being a roster.
    /// </summary>
    [ArchitectureTest]
    public void EveryJudgment_BindsToAtLeastOneOperation()
    {
        var offenders = SharedJoin.Value.Judgment.ServiceList
            .SelectMany(service => service.OperationList.Select(operation => (service.Id, operation.Name, operation.PathList)))
            .Where(entry => entry.PathList.Length == 0)
            .Select(entry => $"{entry.Id}/{entry.Name}")
            .ToArray();

        offenders.Should().BeEmpty(
            "every judgement entry must name at least one esriPath. An entry that binds to no operation is "
            + "invisible to the join in both directions: nothing proves it is true, and nothing notices when it "
            + "stops being true.");
    }

    /// <summary>
    /// Every judgement must say something. An empty note on a partial or stub is a
    /// status with no stated reason, which is exactly the claim a reader cannot verify.
    /// </summary>
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

    /// <summary>
    /// Every <c>evidence</c> path must resolve to a real file — the matrix cited 24 paths
    /// that no longer existed after GeoServices moved assemblies, and a dead path means
    /// a reader cannot verify the claim.
    /// </summary>
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

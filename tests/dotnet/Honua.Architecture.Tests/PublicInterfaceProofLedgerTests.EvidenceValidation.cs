// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Server;
using Xunit;

namespace Honua.Architecture.Tests;

/// <summary>
/// Content-aware validation of the public-interface proof ledger's evidence.
///
/// <para>
/// History: the ledger used to validate every <c>evidenceLocations</c> entry with a bare
/// <see cref="File.Exists(string)"/> check. Existence is not evidence — the check passed
/// identically for a workflow that proves the claim and a document that merely happens to exist,
/// which is how a fabricated GeoServices route (<c>computeClass</c>) rode a green architecture gate
/// to <c>contract-governance: implemented</c> (honua-server#2861/#2864). This is the ADR-0058
/// "fake gate that manufactures confidence", literally. honua-server#2877 replaces the existence
/// check with the gate-aware model below.
/// </para>
///
/// <para>
/// <b>What the model requires.</b> An evidence location falls into one of two tiers:
/// <list type="bullet">
///   <item><b>Gate evidence</b> — an artifact that an <i>automated</i> lane actually executes and
///   that exercises the claim: a test file (architecture / dotnet / python / js), a governance or
///   conformance workflow, a build-enforced source-generated wire contract, or the published gRPC
///   proto schema.</item>
///   <item><b>Corroborating evidence</b> — prose docs (<c>*.md</c>), published data artifacts
///   (<c>*.json</c>), non-gate source, and support files. Necessary context, but never sufficient.</item>
/// </list>
/// Every <c>implemented</c> proof that declares an automated <c>executionLane</c> must cite at least
/// one gate-evidence location <i>whose kind the declared lane runs</i> (an <c>architecture</c> lane
/// must cite an architecture test; a <c>nightly:</c> lane must cite the like-named workflow; and so
/// on). A proof on a purely manual lane (<c>docs+review</c>, <c>release:*</c>) is only admissible as
/// <i>corroboration</i> of a surface that also carries a real automated gate — manual/doc evidence
/// can never be the sole substantiation of a surface.
/// </para>
///
/// <para>
/// <b>False proof prevented.</b> "A document (or model) that merely exists stands in for proof":
/// the exact <c>computeClass</c> failure. A doc/data/plain-source-only proof, or a proof whose cited
/// gate the declared lane does not run, now fails the suite red.
/// </para>
///
/// <para>
/// <b>False proof still admitted (stated deliberately, not silently).</b> This model verifies that a
/// gate the lane runs <i>exists and is of the right kind</i>; it cannot mechanically judge that the
/// gate's assertions are <i>strong enough</i> to cover the full claim, nor that a prose doc's
/// sentences are true. A test that runs in the right lane but asserts less than it should, or a
/// source-generated contract whose companion doc drifts from runtime behaviour, would still pass.
/// Closing that residue requires a claim-specific gate (as honua-server#2879 built for the
/// GeoServices parity matrix), not a generic ledger check. This limitation is documented in
/// <c>docs/internal/contributor/public-interface-quality-model.md</c>.
/// </para>
/// </summary>
public sealed partial class PublicInterfaceProofLedgerTests
{
    [ArchitectureTest]
    public void ProofLedger_EveryImplementedProof_ShouldCiteAGateItsLaneRuns()
    {
        var ledger = LoadLedger();
        var repoRoot = ArchitectureTestHelpers.ResolveRepositoryRoot();

        var failures = ledger.Surfaces
            .SelectMany(surface => surface.Proofs
                .SelectMany(proof => ProofEvidenceValidator.Validate(surface, proof, repoRoot)))
            .ToArray();

        failures.Should().BeEmpty(
            "every proof's cited evidence must actually substantiate the claim, not merely exist " +
            "(honua-server#2877); a document that happens to exist is not proof");
    }

    [ArchitectureTest]
    public void EveryProofLedgerSurface_ShouldMatchAtLeastOneServedRouteOrOperation()
    {
        var ledger = LoadLedger();

        var servedRoutes = EndpointRegistry.All.Select(endpoint => endpoint.Path).ToArray();
        var registeredOperationKeys = OperationRegistry.All
            .Select(operation => FormatOperationKey(operation.Protocol, operation.Operation))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var phantomSurfaces = ledger.Surfaces
            .Where(surface => !string.Equals(surface.SurfaceId, "mcp", StringComparison.OrdinalIgnoreCase))
            .Where(surface =>
                !servedRoutes.Any(route => MatchesEndpoint(surface, route)) &&
                !surface.OperationKeys.Any(key => registeredOperationKeys.Contains(key)))
            .Select(surface => surface.SurfaceId)
            .OrderBy(value => value)
            .ToArray();

        phantomSurfaces.Should().BeEmpty(
            "a proof-ledger surface that matches no served route and no registered operation is an " +
            "over-claim (the 'computeClass' failure at the surface level): the ledger vouches for a " +
            "surface the server does not serve");
    }

    [ArchitectureTest]
    public void EveryProofLedgerOperationKey_ShouldResolveToARegisteredOperation()
    {
        var ledger = LoadLedger();

        var registeredOperationKeys = OperationRegistry.All
            .Select(operation => FormatOperationKey(operation.Protocol, operation.Operation))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var overClaimedOperationKeys = ledger.Surfaces
            .SelectMany(surface => surface.OperationKeys.Select(key => new { surface.SurfaceId, Key = key }))
            .Where(entry => !registeredOperationKeys.Contains(entry.Key))
            .Select(entry => $"{entry.SurfaceId} -> {entry.Key}")
            .OrderBy(value => value)
            .ToArray();

        overClaimedOperationKeys.Should().BeEmpty(
            "every operation key a surface claims must resolve to a real OperationRegistry entry; " +
            "an unresolved key is an operation the ledger vouches for but the server does not register");
    }

    // ---- Demonstration: the mechanism goes red for non-substantiating evidence. ----
    // These exercise the extracted validator directly with synthetic proofs so the guarantee is
    // provable in isolation, independent of the current (compliant) ledger contents.

    [ArchitectureTest]
    public void ProofEvidenceValidator_Rejects_ContractGovernanceProof_WhoseOnlyEvidenceIsADocThatExists()
    {
        var repoRoot = ArchitectureTestHelpers.ResolveRepositoryRoot();

        // A document that genuinely exists on disk — the exact shape the old File.Exists check waved through.
        var proof = MakeProof(
            proofClass: "contract-governance",
            executionLane: "ci:test-all",
            status: "implemented",
            "docs/internal/contributor/public-interface-quality-model.md");
        var surface = MakeSurface("phantom-contract", proof);

        var failures = ProofEvidenceValidator.Validate(surface, proof, repoRoot);

        failures.Should().NotBeEmpty(
            "a contract-governance proof whose only evidence is a document that exists does not " +
            "substantiate the claim and must fail");
        failures.Should().ContainMatch("*executable gate*");
    }

    [ArchitectureTest]
    public void ProofEvidenceValidator_Rejects_NightlyProof_CitingAnUnrelatedWorkflow()
    {
        var repoRoot = ArchitectureTestHelpers.ResolveRepositoryRoot();

        // Declares the WMS CITE conformance lane but cites a workflow that answers a different
        // question — the "workflow that answers an unrelated question" failure from honua-server#2877.
        var proof = MakeProof(
            proofClass: "standards-conformance",
            executionLane: "nightly:cite-wms-conformance",
            status: "implemented",
            ".github/workflows/sdk-server-compatibility.yml");
        var surface = MakeSurface("mislaned-standards", proof);

        var failures = ProofEvidenceValidator.Validate(surface, proof, repoRoot);

        failures.Should().NotBeEmpty(
            "a nightly conformance proof that cites a workflow the declared lane does not run must fail");
    }

    [ArchitectureTest]
    public void ProofEvidenceValidator_Rejects_ManualLaneProof_OnASurfaceWithNoAutomatedGate()
    {
        var repoRoot = ArchitectureTestHelpers.ResolveRepositoryRoot();

        var manualProof = MakeProof(
            proofClass: "contract-governance",
            executionLane: "docs+review",
            status: "implemented",
            "docs/internal/contributor/public-interface-quality-model.md");
        var surface = MakeSurface("docs-only-surface", manualProof);

        var failures = ProofEvidenceValidator.Validate(surface, manualProof, repoRoot);

        failures.Should().NotBeEmpty(
            "a manual-lane proof whose surface carries no automated gate is substantiated by nothing " +
            "but documentation and must fail");
    }

    [ArchitectureTest]
    public void ProofEvidenceValidator_Accepts_ContractGovernanceProof_CitingATestItsLaneRuns()
    {
        var repoRoot = ArchitectureTestHelpers.ResolveRepositoryRoot();

        // Same proof shape as the rejected doc-only one, but repointed at a real architecture gate —
        // the honua-server#2879 fix pattern.
        var proof = MakeProof(
            proofClass: "contract-governance",
            executionLane: "ci:architecture",
            status: "implemented",
            "tests/dotnet/Honua.Architecture.Tests/PublicInterfaceProofLedgerTests.cs");
        var surface = MakeSurface("gated-contract", proof);

        ProofEvidenceValidator.Validate(surface, proof, repoRoot)
            .Should()
            .BeEmpty("a proof repointed at a test its declared lane runs is substantiated");
    }

    [ArchitectureTest]
    public void ProofEvidenceValidator_Accepts_ManualLaneProof_WhenTheSurfaceIsGatedElsewhere()
    {
        var repoRoot = ArchitectureTestHelpers.ResolveRepositoryRoot();

        var manualProof = MakeProof(
            proofClass: "contract-governance",
            executionLane: "docs+review",
            status: "implemented",
            "docs/internal/contributor/public-interface-quality-model.md");
        var gatedSibling = MakeProof(
            proofClass: "route-coverage",
            executionLane: "ci:test-all+architecture",
            status: "implemented",
            "tests/dotnet/Honua.Architecture.Tests/PublicInterfaceProofLedgerTests.cs");
        var surface = MakeSurface("corroborated-surface", manualProof, gatedSibling);

        ProofEvidenceValidator.Validate(surface, manualProof, repoRoot)
            .Should()
            .BeEmpty("a manual-lane proof is admissible as corroboration when its surface carries a real gate");
    }

    private static PublicInterfaceProof MakeProof(
        string proofClass,
        string executionLane,
        string status,
        params string[] evidenceLocations) => new()
        {
            ProofClass = proofClass,
            ProofMechanism = "synthetic proof for validator demonstration",
            ExecutionLane = executionLane,
            EvidenceLocations = evidenceLocations,
            OwnerRepo = "honua-server",
            Status = status,
            LinkedTicket = status == "implemented" ? null : "#0",
        };

    private static PublicInterfaceSurface MakeSurface(string surfaceId, params PublicInterfaceProof[] proofs) => new()
    {
        SurfaceId = surfaceId,
        DisplayName = surfaceId,
        SurfaceKind = "http-route",
        Protocol = "HTTP",
        CanonicalIdentifier = "/synthetic",
        Proofs = proofs,
    };
}

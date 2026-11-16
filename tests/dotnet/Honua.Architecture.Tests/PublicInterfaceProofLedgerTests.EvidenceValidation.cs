// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Server;
using Xunit;

namespace Honua.Architecture.Tests;

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

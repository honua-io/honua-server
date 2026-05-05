// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using FluentAssertions;
using Honua.Server;
using Xunit;

namespace Honua.Architecture.Tests;

/// <summary>
/// Ensures the canonical public-interface proof ledger stays aligned with the
/// shipped runtime surface, release evidence docs, and ownership boundaries.
/// </summary>
[Trait("Category", "Architecture")]
public sealed partial class PublicInterfaceProofLedgerTests
{
    private static readonly HashSet<string> AllowedProofClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "route-coverage",
        "operation-coverage",
        "scenario-depth",
        "contract-governance",
        "standards-conformance",
        "tool-interoperability",
        "real-client-certification"
    };

    private static readonly HashSet<string> AllowedStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "implemented",
        "planned",
        "bounded-child-ticket"
    };

    private static readonly HashSet<string> SdkCompatibilitySurfaceIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "control-plane-admin",
        "feature-server",
        "ogc-api-features"
    };

    private static readonly Dictionary<string, string> SdkIntegrationTicketsByOwnerRepo = new(StringComparer.OrdinalIgnoreCase)
    {
        ["honua-sdk-js"] = "#39",
        ["honua-sdk-dotnet"] = "#31",
        ["honua-sdk-python"] = "#21"
    };

    private static readonly Regex MarkdownLinkRegex =
        new(@"\[[^\]]+\]\((?<path>[^)]+)\)", RegexOptions.Compiled);

    [ArchitectureTest]
    public void EveryEndpointRegistryRoute_ShouldBeCoveredByProofLedgerSurface()
    {
        var ledger = LoadLedger();

        var uncovered = EndpointRegistry.All
            .Where(endpoint => !ledger.Surfaces.Any(surface => MatchesEndpoint(surface, endpoint.Path)))
            .Select(endpoint => $"{endpoint.Method} {endpoint.Path}")
            .OrderBy(value => value)
            .ToArray();

        uncovered.Should().BeEmpty(
            "every shipped endpoint must map to at least one surface in docs/gis/data/public-interface-proof.json");
    }

    [ArchitectureTest]
    public void EveryRegisteredOperation_ShouldBeCoveredByProofLedgerSurface()
    {
        var ledger = LoadLedger();

        var uncovered = OperationRegistry.All
            .Select(operation => FormatOperationKey(operation.Protocol, operation.Operation))
            .Where(operationKey => !ledger.Surfaces.Any(surface => surface.OperationKeys.Contains(operationKey, StringComparer.OrdinalIgnoreCase)))
            .OrderBy(value => value)
            .ToArray();

        uncovered.Should().BeEmpty(
            "every registered public-interface operation must map to at least one surface in docs/gis/data/public-interface-proof.json");
    }

    [ArchitectureTest]
    public void ProofLedger_ShouldDeclareRequiredMetadata_ForEveryProof()
    {
        var ledger = LoadLedger();
        var repoRoot = ArchitectureTestHelpers.ResolveRepositoryRoot();

        ledger.SchemaVersion.Should().Be("1.0.0");
        ledger.Surfaces.Should().NotBeEmpty();
        ledger.Surfaces.Select(surface => surface.SurfaceId)
            .Should()
            .OnlyHaveUniqueItems("surface ids are the canonical keys in the proof ledger");

        var knownSurfaceIds = ledger.Surfaces.Select(surface => surface.SurfaceId).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var surface in ledger.Surfaces)
        {
            surface.SurfaceId.Should().NotBeNullOrWhiteSpace();
            surface.DisplayName.Should().NotBeNullOrWhiteSpace();
            surface.SurfaceKind.Should().NotBeNullOrWhiteSpace();
            surface.Protocol.Should().NotBeNullOrWhiteSpace();
            surface.CanonicalIdentifier.Should().NotBeNullOrWhiteSpace();
            surface.Proofs.Should().NotBeEmpty();

            if (!string.IsNullOrWhiteSpace(surface.ParentSurfaceId))
            {
                knownSurfaceIds.Should().Contain(surface.ParentSurfaceId!,
                    $"parent surface '{surface.ParentSurfaceId}' for '{surface.SurfaceId}' must exist");
            }

            var selectorCount = surface.EndpointPrefixes.Length + surface.EndpointExactMatches.Length + surface.EndpointContains.Length + surface.OperationKeys.Length;
            if (!string.Equals(surface.SurfaceId, "mcp", StringComparison.OrdinalIgnoreCase))
            {
                selectorCount.Should().BeGreaterThan(0, $"surface '{surface.SurfaceId}' must declare at least one route or operation selector");
            }

            foreach (var proof in surface.Proofs)
            {
                AllowedProofClasses.Should().Contain(proof.ProofClass,
                    $"proof class '{proof.ProofClass}' on surface '{surface.SurfaceId}' is not part of the canonical model");
                AllowedStatuses.Should().Contain(proof.Status,
                    $"status '{proof.Status}' on surface '{surface.SurfaceId}' is not part of the canonical model");

                proof.ProofMechanism.Should().NotBeNullOrWhiteSpace();
                proof.ExecutionLane.Should().NotBeNullOrWhiteSpace();
                proof.OwnerRepo.Should().NotBeNullOrWhiteSpace();
                proof.EvidenceLocations.Should().NotBeEmpty();

                foreach (var evidenceLocation in proof.EvidenceLocations)
                {
                    evidenceLocation.Should().NotBeNullOrWhiteSpace();
                    if (IsExternalEvidenceLocation(evidenceLocation))
                    {
                        continue;
                    }

                    File.Exists(Path.Combine(repoRoot, evidenceLocation))
                        .Should()
                        .BeTrue($"evidence location '{evidenceLocation}' for surface '{surface.SurfaceId}' must exist");
                }

                if (!string.Equals(proof.Status, "implemented", StringComparison.OrdinalIgnoreCase))
                {
                    proof.LinkedTicket.Should().NotBeNullOrWhiteSpace(
                        $"unfinished proof '{proof.ProofClass}' on surface '{surface.SurfaceId}' must point at a follow-up ticket");
                }

                if (string.Equals(proof.ProofClass, "tool-interoperability", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(proof.ProofClass, "real-client-certification", StringComparison.OrdinalIgnoreCase))
                {
                    proof.VersionCaptureRule.Should().NotBeNullOrWhiteSpace(
                        $"proof '{proof.ProofClass}' on surface '{surface.SurfaceId}' must explain how the tool or client version is captured");
                }
            }
        }
    }

    [ArchitectureTest]
    public void ProofLedger_ShouldExplicitlyRepresentAllGovernanceBoundaries()
    {
        var ledger = LoadLedger();

        var proofClasses = ledger.Surfaces
            .SelectMany(surface => surface.Proofs)
            .Select(proof => proof.ProofClass)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var missingProofClasses = AllowedProofClasses
            .Where(proofClass => !proofClasses.Contains(proofClass))
            .OrderBy(proofClass => proofClass)
            .ToArray();

        missingProofClasses.Should().BeEmpty(
            "the canonical proof model must keep route/operation coverage, scenario depth, contract governance, standards conformance, tool interoperability, and real-client certification distinct");
    }

    [ArchitectureTest]
    public void ClientTemplateVersionMatrix_ShouldUseConcreteEvidenceLinks()
    {
        var repoRoot = ArchitectureTestHelpers.ResolveRepositoryRoot();
        var matrixPath = Path.Combine(repoRoot, "docs", "gis", "CLIENT_TEMPLATE_VERSION_MATRIX.md");
        var matrixDirectory = Path.GetDirectoryName(matrixPath)!;

        var evidenceRows = File.ReadAllLines(matrixPath)
            .Where(line => line.StartsWith("| ", StringComparison.Ordinal) && line.Contains(".cert.json", StringComparison.Ordinal))
            .ToArray();

        evidenceRows.Should().HaveCount(8, "the current release ledger should enumerate ArcGIS Pro, QGIS, Power BI, Excel, MapLibre, and PyQGIS (OGC/WFS) evidence rows");

        foreach (var row in evidenceRows)
        {
            row.Should().NotContain("TBD",
                "required client rows must point at concrete immutable evidence instead of placeholders");

            var links = MarkdownLinkRegex.Matches(row)
                .Select(match => match.Groups["path"].Value)
                .ToArray();

            links.Should().NotBeEmpty("each client evidence row must include at least one immutable link");

            foreach (var link in links)
            {
                if (link.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                    link.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    continue; // External artifact URLs are valid on release branches
                }

                var resolvedPath = Path.GetFullPath(Path.Combine(matrixDirectory, link));
                File.Exists(resolvedPath)
                    .Should()
                    .BeTrue($"matrix link '{link}' must resolve to a committed immutable evidence artifact");
            }
        }
    }

    [ArchitectureTest]
    public void OnlyApprovedProofs_MayPointToExternalOwnerRepos()
    {
        var ledger = LoadLedger();

        var externalProofs = ledger.Surfaces
            .SelectMany(surface => surface.Proofs.Select(proof => new { surface.SurfaceId, Proof = proof }))
            .Where(entry => !string.Equals(entry.Proof.OwnerRepo, "honua-server", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        externalProofs.Should().NotBeEmpty("bounded child ownership should remain visible in the proof ledger");
        externalProofs.Should().OnlyContain(entry => IsApprovedExternalProof(entry.SurfaceId, entry.Proof),
            "only approved bounded-child surfaces may point at external owner repos");
    }

    private static PublicInterfaceProofLedger LoadLedger()
    {
        var repoRoot = ArchitectureTestHelpers.ResolveRepositoryRoot();
        var ledgerPath = Path.Combine(repoRoot, "docs", "gis", "data", "public-interface-proof.json");

        using var stream = File.OpenRead(ledgerPath);
        return JsonSerializer.Deserialize(stream, PublicInterfaceProofLedgerJsonContext.Default.PublicInterfaceProofLedger)
            ?? throw new InvalidOperationException("Unable to deserialize public-interface-proof.json.");
    }

    private static bool MatchesEndpoint(PublicInterfaceSurface surface, string path)
        => surface.EndpointExactMatches.Contains(path, StringComparer.OrdinalIgnoreCase)
        || surface.EndpointPrefixes.Any(prefix => path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        || surface.EndpointContains.Any(value => path.Contains(value, StringComparison.OrdinalIgnoreCase));

    private static bool IsApprovedExternalProof(string surfaceId, PublicInterfaceProof proof)
    {
        if (string.Equals(surfaceId, "mcp", StringComparison.OrdinalIgnoreCase))
        {
            return string.Equals(proof.OwnerRepo, "honua-sdk-js", StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(proof.LinkedTicket, "#484", StringComparison.Ordinal);
        }

        if (string.Equals(surfaceId, "grpc-feature-service", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(surfaceId, "grpc-process-service", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(surfaceId, "spec-engine", StringComparison.OrdinalIgnoreCase))
        {
            return string.Equals(proof.ProofClass, "contract-governance", StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(proof.OwnerRepo, "geospatial-grpc", StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(proof.ExecutionLane, "repo:honua-io/geospatial-grpc", StringComparison.OrdinalIgnoreCase) &&
                   proof.EvidenceLocations.Any(IsGeospatialGrpcRepositoryUrl);
        }

        if (SdkCompatibilitySurfaceIds.Contains(surfaceId))
        {
            return string.Equals(proof.ProofClass, "tool-interoperability", StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(proof.ExecutionLane, "nightly:sdk-server-compatibility", StringComparison.OrdinalIgnoreCase) &&
                   SdkIntegrationTicketsByOwnerRepo.TryGetValue(proof.OwnerRepo, out var linkedTicket) &&
                   string.Equals(proof.LinkedTicket, linkedTicket, StringComparison.Ordinal) &&
                   proof.EvidenceLocations.Any(location => IsSdkIntegrationIssueUrl(location, proof.OwnerRepo, linkedTicket));
        }

        return false;
    }

    private static bool IsExternalEvidenceLocation(string evidenceLocation)
        => Uri.TryCreate(evidenceLocation, UriKind.Absolute, out var uri) &&
           (string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase));

    private static bool IsGeospatialGrpcRepositoryUrl(string evidenceLocation)
        => Uri.TryCreate(evidenceLocation, UriKind.Absolute, out var uri) &&
           string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase) &&
           string.Equals(uri.AbsolutePath.TrimEnd('/'), "/honua-io/geospatial-grpc", StringComparison.OrdinalIgnoreCase);

    private static bool IsSdkIntegrationIssueUrl(string evidenceLocation, string ownerRepo, string linkedTicket)
    {
        if (!Uri.TryCreate(evidenceLocation, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var issueNumber = linkedTicket.TrimStart('#');
        return string.Equals(
            uri.AbsolutePath.TrimEnd('/'),
            $"/honua-io/{ownerRepo}/issues/{issueNumber}",
            StringComparison.OrdinalIgnoreCase);
    }

    private static string FormatOperationKey(string protocol, string operation) =>
        $"{protocol}::{operation}";
}

internal sealed class PublicInterfaceProofLedger
{
    public string SchemaVersion { get; init; } = string.Empty;

    public string UpdatedUtc { get; init; } = string.Empty;

    public PublicInterfaceSurface[] Surfaces { get; init; } = [];
}

internal sealed class PublicInterfaceSurface
{
    public string SurfaceId { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;

    public string SurfaceKind { get; init; } = string.Empty;

    public string Protocol { get; init; } = string.Empty;

    public string CanonicalIdentifier { get; init; } = string.Empty;

    public string? ParentSurfaceId { get; init; }

    public string[] EndpointPrefixes { get; init; } = [];

    public string[] EndpointExactMatches { get; init; } = [];

    public string[] EndpointContains { get; init; } = [];

    public string[] OperationKeys { get; init; } = [];

    public PublicInterfaceProof[] Proofs { get; init; } = [];
}

internal sealed class PublicInterfaceProof
{
    public string ProofClass { get; init; } = string.Empty;

    public string ProofMechanism { get; init; } = string.Empty;

    public string ExecutionLane { get; init; } = string.Empty;

    public string[] EvidenceLocations { get; init; } = [];

    public string OwnerRepo { get; init; } = string.Empty;

    public string Status { get; init; } = string.Empty;

    public string? LinkedTicket { get; init; }

    public string? VersionCaptureRule { get; init; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(PublicInterfaceProofLedger))]
internal sealed partial class PublicInterfaceProofLedgerJsonContext : JsonSerializerContext
{
}

// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
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
        "ogc-api-features",
        "migration-scan",
        "arcgis-import",
        "geoserver-dry-run",
        "migration-evidence"
    };

    private static readonly HashSet<string> SdkCompatibilityOwnerRepos = new(StringComparer.OrdinalIgnoreCase)
    {
        "honua-sdk-js",
        "honua-sdk-dotnet",
        "honua-sdk-python"
    };

    private static readonly Dictionary<string, Dictionary<string, string>> SdkIntegrationTicketsBySurfaceAndOwnerRepo = new(StringComparer.OrdinalIgnoreCase)
    {
        ["control-plane-admin"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["honua-sdk-js"] = "#39",
            ["honua-sdk-dotnet"] = "#31",
            ["honua-sdk-python"] = "#21"
        },
        ["feature-server"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["honua-sdk-js"] = "#39",
            ["honua-sdk-dotnet"] = "#31",
            ["honua-sdk-python"] = "#21"
        },
        ["ogc-api-features"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["honua-sdk-js"] = "#39",
            ["honua-sdk-dotnet"] = "#31",
            ["honua-sdk-python"] = "#21"
        },
        ["migration-scan"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["honua-sdk-js"] = "#105",
            ["honua-sdk-dotnet"] = "#134",
            ["honua-sdk-python"] = "#49"
        },
        ["arcgis-import"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["honua-sdk-js"] = "#105",
            ["honua-sdk-dotnet"] = "#134",
            ["honua-sdk-python"] = "#49"
        },
        ["geoserver-dry-run"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["honua-sdk-js"] = "#105",
            ["honua-sdk-dotnet"] = "#134",
            ["honua-sdk-python"] = "#49"
        },
        ["migration-evidence"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["honua-sdk-js"] = "#105",
            ["honua-sdk-dotnet"] = "#134",
            ["honua-sdk-python"] = "#49"
        }
    };

    private static readonly Dictionary<string, HashSet<string>> ImplementedSdkCompatibilitySurfaceIdsByOwnerRepo = new(StringComparer.OrdinalIgnoreCase)
    {
        ["honua-sdk-js"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "control-plane-admin",
            "feature-server",
            "ogc-api-features",
            "migration-scan",
            "arcgis-import",
            "geoserver-dry-run",
            "migration-evidence"
        },
        ["honua-sdk-python"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "control-plane-admin",
            "feature-server"
        },
        ["honua-sdk-dotnet"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "control-plane-admin",
            "migration-scan"
        }
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

    [ArchitectureTest]
    public void SdkCompatibilityProofs_ShouldNotOverstateImplementedServerMatrixSurfaces()
    {
        var ledger = LoadLedger();

        var sdkProofs = ledger.Surfaces
            .Where(surface => SdkCompatibilitySurfaceIds.Contains(surface.SurfaceId))
            .SelectMany(surface => surface.Proofs
                .Where(proof => SdkCompatibilityOwnerRepos.Contains(proof.OwnerRepo))
                .Select(proof => new { surface.SurfaceId, Proof = proof }))
            .ToArray();

        var expectedPairs = SdkCompatibilityOwnerRepos
            .SelectMany(ownerRepo => SdkCompatibilitySurfaceIds.Select(surfaceId => $"{ownerRepo}:{surfaceId}"))
            .ToArray();

        sdkProofs.Select(entry => $"{entry.Proof.OwnerRepo}:{entry.SurfaceId}")
            .Should()
            .BeEquivalentTo(expectedPairs,
                "the SDK compatibility ledger must keep both implemented and bounded-child SDK surfaces visible");

        foreach (var entry in sdkProofs)
        {
            var isImplementedSurface =
                ImplementedSdkCompatibilitySurfaceIdsByOwnerRepo.TryGetValue(entry.Proof.OwnerRepo, out var implementedSurfaceIds) &&
                implementedSurfaceIds.Contains(entry.SurfaceId);

            if (isImplementedSurface)
            {
                entry.Proof.Status.Should().Be("implemented",
                    $"{entry.Proof.OwnerRepo} currently exercises '{entry.SurfaceId}' in sdk-server-compatibility.yml");
            }
            else
            {
                entry.Proof.Status.Should().Be("bounded-child-ticket",
                    $"{entry.Proof.OwnerRepo} must not mark '{entry.SurfaceId}' implemented until the SDK lane exercises it");
            }
        }
    }

    [ArchitectureTest]
    public void SdkCompatibilityWorkflow_ShouldResolveEvidenceIdentityFromCheckedOutSources()
    {
        var workflow = LoadSdkCompatibilityWorkflow();

        workflow.Should().Contain("git rev-parse HEAD",
            "server_commit must be the resolved checkout SHA, not the requested branch or tag ref");
        workflow.Should().Contain("--arg serverCommit \"$server_commit\"");
        workflow.Should().Contain("server_commit: $serverCommit");
        workflow.Should().Contain("commit: $serverCommit");
        workflow.Should().NotContain("server_commit: $serverCheckoutRef");
        workflow.Should().NotContain("commit: $serverCheckoutRef");

        workflow.Should().Contain("dotnet msbuild \"$path\" -nologo -v:q -getProperty:\"$property_name\"",
            ".NET package versions must be evaluated through MSBuild so Directory.Build.props is honored");
    }

    [ArchitectureTest]
    public void SdkCompatibilityWorkflow_ShouldConvertSmokeTimeoutsIntoCellEvidence()
    {
        var workflow = LoadSdkCompatibilityWorkflow();
        const int RequiredSetupAndEvidenceBudgetSeconds = 30 * 60;

        var jobTimeoutSeconds = ExtractRequiredInt32(
            SdkCompatibilityMatrixJobTimeoutRegex().Match(workflow),
            "minutes") * 60;
        var smokeTimeoutSeconds = ExtractRequiredInt32(
            SdkCompatibilitySmokeTimeoutRegex().Match(workflow),
            "seconds");
        var killAfterSeconds = ExtractRequiredInt32(
            SdkCompatibilityKillAfterRegex().Match(workflow),
            "seconds");

        workflow.Should().Contain("timeout --kill-after=30s \"$SDK_COMPATIBILITY_TIMEOUT_SECONDS\"",
            "the smoke command should time out before the job timeout so the evidence writer still runs");
        jobTimeoutSeconds.Should().BeGreaterThanOrEqualTo(
            smokeTimeoutSeconds + killAfterSeconds + RequiredSetupAndEvidenceBudgetSeconds,
            "the job-level timeout must leave setup, kill-grace, and evidence-writing budget after the smoke timeout");
        workflow.Should().Contain("timed out after %ss.");
        workflow.Should().Contain("- name: Write compatibility result\n        if: always()");
        workflow.Should().Contain("- name: Upload SDK compatibility cell evidence\n        if: always()");
        workflow.Should().Contain("always() && matrix.expected_supported == true && steps.compatibility.outcome != 'success'");
        workflow.Should().Contain("COMPATIBILITY_EXIT_CODE: ${{ steps.compatibility.outputs.exit_code }}");
        workflow.Should().Contain("failure_diagnostics");
    }

    [ArchitectureTest]
    public void SdkCompatibilityWorkflow_ShouldUseProofLedgerSurfaceIdsInProtocolEvidence()
    {
        var ledger = LoadLedger();
        var workflow = LoadSdkCompatibilityWorkflow();

        var knownSurfaceIds = ledger.Surfaces
            .Select(surface => surface.SurfaceId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var protocolSurfaces = ExtractWorkflowProtocolSurfaceArray(workflow, "protocol_surfaces");
        var protocolSurfacesBySdk = ExtractWorkflowProtocolSurfacesBySdk(workflow);

        protocolSurfaces.Should().Contain([
            "control-plane-admin",
            "platform-http",
            "geoservices-catalog",
            "feature-server",
            "ogc-api-features",
            "migration-scan",
            "arcgis-import",
            "geoserver-dry-run",
            "migration-evidence"
        ]);
        protocolSurfacesBySdk.Keys.Should().BeEquivalentTo(["js", "python", "dotnet"],
            "compat-result.json should preserve per-SDK protocol-surface diagnostics");

        var aggregateSurfaceIds = protocolSurfacesBySdk.Values
            .SelectMany(surfaceIds => surfaceIds)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(surfaceId => surfaceId, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        protocolSurfaces
            .OrderBy(surfaceId => surfaceId, StringComparer.OrdinalIgnoreCase)
            .Should()
            .Equal(aggregateSurfaceIds,
                "the aggregate protocol_surfaces list must reconcile to protocol_surfaces_by_sdk");

        var emittedSurfaceIds = protocolSurfaces
            .Concat(protocolSurfacesBySdk.Values.SelectMany(surfaceIds => surfaceIds))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(surfaceId => surfaceId, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var unknownSurfaceIds = emittedSurfaceIds
            .Where(surfaceId => !knownSurfaceIds.Contains(surfaceId))
            .ToArray();

        unknownSurfaceIds.Should().BeEmpty(
            "compat-result.json protocol surfaces should align with public-interface-proof.json surface ids");
    }

    [ArchitectureTest]
    public void SdkCompatibilityWorkflow_ShouldRecordMigrationAutomationSurfaceStatusBySdk()
    {
        var workflow = LoadSdkCompatibilityWorkflow();

        workflow.Should().Contain("migration_automation: {");
        workflow.Should().Contain("migration_automation_by_sdk: {");
        workflow.Should().Contain("results/migration-automation.json");
        workflow.Should().Contain("--argjson migrationAutomation \"$migration_automation_json\"");
        workflow.Should().Contain("migration_automation: $migrationAutomation.migration_automation");
        workflow.Should().Contain("migration_automation_by_sdk: $migrationAutomation.migration_automation_by_sdk");
        workflow.Should().Contain("required: false");
        workflow.Should().Contain("status: \"failed\"",
            "missing migration smoke output should be recorded as a failed attempted smoke, not default unsupported evidence");
        workflow.Should().NotContain("status: \"unsupported\",\n                passed: false,\n                reason: \"Server compatibility artifacts declare migration automation surfaces",
            "the workflow must consume real SDK-backed smoke evidence instead of hardcoded default unsupported evidence");

        foreach (var surfaceId in new[]
                 {
                     "migration-scan",
                     "arcgis-import",
                     "geoserver-dry-run",
                     "migration-evidence"
                 })
        {
            workflow.Should().Contain($"failed_surface(\"{surfaceId}\"",
                $"{surfaceId} must be visible in fallback compat-result.json diagnostics if migration smoke evidence is missing");
        }

        workflow.Should().Contain("honua-sdk-js#105");
        workflow.Should().Contain("honua-sdk-python#49");
        workflow.Should().Contain("honua-sdk-dotnet#134");
    }

    private static PublicInterfaceProofLedger LoadLedger()
    {
        var repoRoot = ArchitectureTestHelpers.ResolveRepositoryRoot();
        var ledgerPath = Path.Combine(repoRoot, "docs", "gis", "data", "public-interface-proof.json");

        using var stream = File.OpenRead(ledgerPath);
        return JsonSerializer.Deserialize(stream, PublicInterfaceProofLedgerJsonContext.Default.PublicInterfaceProofLedger)
            ?? throw new InvalidOperationException("Unable to deserialize public-interface-proof.json.");
    }

    private static string LoadSdkCompatibilityWorkflow()
    {
        var repoRoot = ArchitectureTestHelpers.ResolveRepositoryRoot();
        return File.ReadAllText(Path.Combine(repoRoot, ".github", "workflows", "sdk-server-compatibility.yml"));
    }

    private static string[] ExtractWorkflowProtocolSurfaceArray(string workflow, string propertyName)
    {
        var propertyIndex = workflow.IndexOf($"{propertyName}: [", StringComparison.Ordinal);
        if (propertyIndex < 0)
        {
            throw new InvalidOperationException($"Unable to find '{propertyName}' in sdk-server-compatibility.yml.");
        }

        var arrayStartIndex = workflow.IndexOf('[', propertyIndex);
        var arrayEndIndex = workflow.IndexOf(']', arrayStartIndex);
        if (arrayStartIndex < 0 || arrayEndIndex < 0)
        {
            throw new InvalidOperationException($"Unable to parse '{propertyName}' in sdk-server-compatibility.yml.");
        }

        return ExtractQuotedSurfaceIds(workflow[arrayStartIndex..(arrayEndIndex + 1)]);
    }

    private static Dictionary<string, string[]> ExtractWorkflowProtocolSurfacesBySdk(string workflow)
    {
        const string PropertyName = "protocol_surfaces_by_sdk";

        var propertyIndex = workflow.IndexOf($"{PropertyName}: {{", StringComparison.Ordinal);
        if (propertyIndex < 0)
        {
            throw new InvalidOperationException($"Unable to find '{PropertyName}' in sdk-server-compatibility.yml.");
        }

        var blockEndIndex = workflow.IndexOf("migration_automation:", propertyIndex, StringComparison.Ordinal);
        if (blockEndIndex < 0)
        {
            throw new InvalidOperationException($"Unable to parse '{PropertyName}' in sdk-server-compatibility.yml.");
        }

        var block = workflow[propertyIndex..blockEndIndex];
        var surfacesBySdk = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

        foreach (Match match in SdkProtocolSurfaceBlockRegex().Matches(block))
        {
            var sdkName = match.Groups["sdk"].Value;
            surfacesBySdk[sdkName] = ExtractQuotedSurfaceIds(match.Groups["surfaces"].Value);
        }

        return surfacesBySdk;
    }

    private static string[] ExtractQuotedSurfaceIds(string text) =>
        QuotedSurfaceIdRegex().Matches(text)
            .Select(match => match.Groups["surfaceId"].Value)
            .ToArray();

    private static int ExtractRequiredInt32(Match match, string groupName)
    {
        match.Success.Should().BeTrue($"sdk-server-compatibility.yml must declare '{groupName}' for timeout evidence validation");
        match.Groups[groupName].Success.Should().BeTrue($"sdk-server-compatibility.yml must expose '{groupName}' for timeout evidence validation");
        return int.Parse(match.Groups[groupName].Value, CultureInfo.InvariantCulture);
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
            string.Equals(surfaceId, "grpc-scene-service", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(surfaceId, "grpc-tile-service", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(surfaceId, "grpc-elevation-service", StringComparison.OrdinalIgnoreCase) ||
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
                   TryGetSdkIntegrationTicket(surfaceId, proof.OwnerRepo, out var linkedTicket) &&
                   string.Equals(proof.LinkedTicket, linkedTicket, StringComparison.Ordinal) &&
                   proof.EvidenceLocations.Any(location => IsSdkIntegrationIssueUrl(location, proof.OwnerRepo, linkedTicket));
        }

        return false;
    }

    private static bool TryGetSdkIntegrationTicket(string surfaceId, string ownerRepo, out string linkedTicket)
    {
        linkedTicket = string.Empty;
        if (!SdkIntegrationTicketsBySurfaceAndOwnerRepo.TryGetValue(surfaceId, out var ticketsByOwnerRepo))
        {
            return false;
        }

        return ticketsByOwnerRepo.TryGetValue(ownerRepo, out linkedTicket!);
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

    [GeneratedRegex(@"(?<sdk>js|python|dotnet):\s*\[(?<surfaces>.*?)\]", RegexOptions.Singleline)]
    private static partial Regex SdkProtocolSurfaceBlockRegex();

    [GeneratedRegex(@"sdk-compat-matrix:[\s\S]*?timeout-minutes:\s*(?<minutes>\d+)")]
    private static partial Regex SdkCompatibilityMatrixJobTimeoutRegex();

    [GeneratedRegex(@"SDK_COMPATIBILITY_TIMEOUT_SECONDS:\s*'(?<seconds>\d+)'")]
    private static partial Regex SdkCompatibilitySmokeTimeoutRegex();

    [GeneratedRegex(@"timeout --kill-after=(?<seconds>\d+)s ""\$SDK_COMPATIBILITY_TIMEOUT_SECONDS""")]
    private static partial Regex SdkCompatibilityKillAfterRegex();

    [GeneratedRegex("\"(?<surfaceId>[a-z0-9.-]+)\"")]
    private static partial Regex QuotedSurfaceIdRegex();
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

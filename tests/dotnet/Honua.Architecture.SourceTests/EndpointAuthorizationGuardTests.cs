// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace Honua.Architecture.Tests;

[Trait("Category", "Architecture")]
public sealed partial class EndpointAuthorizationGuardTests
{
    private static readonly string[] FeaturesRelativePaths = ["src/Honua.Server/Features", "src/Honua.Ai/Features"];
    private const string ProgramRelativePath = "src/Honua.Server/Program.cs";
    private const string BaselineRelativePath = "tests/dotnet/Honua.Architecture.SourceTests/endpoint-authorization-baseline.txt";
    private static readonly string[] MutationVerbs = ["MapPost", "MapPut", "MapDelete", "MapPatch"];
    private static readonly string[] BaselinedVerbs = ["MapGet", "MapMethods"];

    // Mutation routes that are reachable without credentials on purpose and authorize inside the
    // handler. The MCP dispatcher admits the anonymous protocol handshake and checks each tool's
    // scope and approval rules per call. Add an entry only with that kind of in-handler check.
    private static readonly string[] ReviewedAnonymousMutationRoutes =
    [
        "src/Honua.Ai/Features/Protocols/Mcp/Mcp/McpEndpointExtensions.cs MapDelete RoutePath",
        "src/Honua.Ai/Features/Protocols/Mcp/Mcp/McpEndpointExtensions.cs MapPost RoutePath",
    ];

    private static readonly string[] AuthOptInMarkers =
    {
        "RequireAuthorization",
        "RequireAdminAuthorization",
        // Narrow operation-approval policy: admits only full administrators
        // or scoped admin API keys carrying the explicit admin:approve grant.
        "RequireAdminApproveAuthorization",
        "RequireAdminApiKey",
        "RequirePermission",
        "RequireRole",
        "RequireScope",
        "AllowAnonymous",
        // Temporal history auth helpers wrap RequireAuthorization with a distinct
        // policy per surface (history read, diff read, rollback execute); recognize them explicitly.
        "RequireTemporalHistoryRead",
        "RequireTemporalDiffRead",
        "RequireTemporalRollbackExecute",
        // Studio lifecycle policy: method-scoped admin grants plus flag-gated end-user ownership
        // checks performed by StudioEndpointAuthorization in each handler.
        "RequireStudioLifecycleAuthorization",
        // Studio AI proxy policy: composes the lifecycle flag gate with the stricter interactive
        // principal boundary used before any model-provider call.
        "RequireStudioAiProxyAuthorization",
        // Read-only ops-reader policy (A12): a method-aware Require*Authorization helper that admits
        // ops:read/admin for safe reads while still requiring full admin write for mutating routes.
        "RequireOpsReadAuthorization",
        // Controlled-conformance mutation policy: a narrowly scoped write
        // credential for the scheduled SDK conformance runner, additionally gated per run by an
        // ownership token checked inside each handler.
        "RequireConformanceMutateAuthorization",
    };

    [ArchitectureTest]
    public void EveryMutationEndpoint_HasExplicitAuthorizationDecision()
    {
        var repoRoot = ArchitectureTestHelpers.ResolveRepositoryRoot();

        var found = FindUnmarkedRoutes(repoRoot, MutationVerbs);
        var gaps = SubtractMultiset(found, ReviewedAnonymousMutationRoutes);
        var stale = SubtractMultiset(ReviewedAnonymousMutationRoutes, found);

        stale.Should().BeEmpty("reviewed anonymous mutation routes must still exist unmarked: " + string.Join("; ", stale));
        gaps.Should().BeEmpty(
            "every HTTP mutation route in any src project must call one of "
            + string.Join(", ", AuthOptInMarkers)
            + " on the route itself, its parent group or the group passed into its mapping method; intentionally "
            + "public endpoints must opt in via AllowAnonymous() so the decision is visible to reviewers. Unmarked: "
            + string.Join("; ", gaps));
    }

    [ArchitectureTest]
    public void EveryReadEndpoint_HasExplicitAuthorizationDecisionOrReviewedBaselineEntry()
    {
        var repoRoot = ArchitectureTestHelpers.ResolveRepositoryRoot();
        var gaps = FindUnmarkedRoutes(repoRoot, BaselinedVerbs);
        var baseline = ReadBaseline(repoRoot);

        var unreviewed = SubtractMultiset(gaps, baseline);
        var stale = SubtractMultiset(baseline, gaps);

        unreviewed.Should().BeEmpty(
            "there is no fallback authorization policy, so a GET or MapMethods route without an authorization "
            + "marker is anonymous unless its handler checks access; mark the route or group, or add the route to "
            + BaselineRelativePath + " after confirming the handler's access check. Unreviewed: "
            + string.Join("; ", unreviewed));
        stale.Should().BeEmpty(
            "baseline entries must match an unmarked route; remove entries for routes that were marked, renamed "
            + "or deleted from " + BaselineRelativePath + ". Stale: " + string.Join("; ", stale));
    }

    [ArchitectureTest]
    public void GrpcSpecServiceMapping_RequiresCanonicalAdminAuthorization()
    {
        var repoRoot = ArchitectureTestHelpers.ResolveRepositoryRoot();
        var programPath = ArchitectureTestHelpers.CombinePath(repoRoot, ProgramRelativePath);
        var program = File.ReadAllText(programPath);

        var authorizedMapping = new Regex(
            @"MapGrpcService<Honua\.Server\.Features\.Spec\.HonuaSpecService>\s*\(\s*\)\s*\.RequireAdminAuthorization\s*\(\s*\)",
            RegexOptions.CultureInvariant);

        authorizedMapping.IsMatch(program).Should().BeTrue(
            "geospatial.v1.SpecService plan/apply/cancel operations must inherit the same "
            + "Admin authorization policy as their POST-based REST twins before handler execution");
    }

    [ArchitectureTest]
    public void AuditedFeaturesDirectory_ContainsEndpointFiles()
    {
        var repoRoot = ArchitectureTestHelpers.ResolveRepositoryRoot();
        var featureRoots = FeaturesRelativePaths.Select(path => ArchitectureTestHelpers.CombinePath(repoRoot, path)).ToArray();
        foreach (var featuresRoot in featureRoots)
        {
            Directory.EnumerateFiles(featuresRoot, "*Endpoints.cs", SearchOption.AllDirectories).Should().NotBeEmpty();
        }

        var fileCount = featureRoots.Sum(path => Directory.EnumerateFiles(path, "*Endpoints.cs", SearchOption.AllDirectories).Count());

        fileCount.Should().BeGreaterThan(50,
            "the architecture guard depends on scanning *Endpoints.cs files in src/Honua.Server/Features; "
            + "a count this low usually means the directory layout changed and the regex needs to follow.");
    }
}

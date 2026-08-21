// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Ai.Protocols.Mcp.Tools;
using Honua.Server.Features.Operations.Admin;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Protocols.Mcp;

/// <summary>
/// Generates and drift-gates the cross-repository Admin API to MCP coverage contract.
/// Consumers must use the checked-in artifact rather than copying a point-in-time tool count.
/// </summary>
public sealed class AdminMcpCoverageArtifactTests
{
    private const string EmitEnvironmentVariable = "HONUA_EMIT_ADMIN_MCP_COVERAGE";
    private const string RelativeArtifactPath = "docs/developer/api-specs/admin-mcp-coverage.v1.json";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    [UnitTest]
    public void CommittedCoverage_EqualsIntegratedAdminCatalogProjection()
    {
        var generated = GenerateCoverage();
        var artifactPath = ResolveRepositoryPath(RelativeArtifactPath);

        if (string.Equals(
                Environment.GetEnvironmentVariable(EmitEnvironmentVariable),
                "1",
                StringComparison.Ordinal))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(artifactPath)!);
            File.WriteAllText(artifactPath, generated);
        }

        File.Exists(artifactPath).Should().BeTrue(
            $"the generated Admin MCP coverage contract must exist at {RelativeArtifactPath}");
        File.ReadAllText(artifactPath).Should().Be(
            generated,
            "every Admin OpenAPI operation must remain machine-readably projected or explicitly excluded; "
            + "run scripts/generate-admin-mcp-coverage.sh and commit the result");
    }

    private static string GenerateCoverage()
    {
        var catalog = new AdminOpenApiOperationCatalog(ResolveRepositoryPath("docs/developer/api-specs/admin-api.json"));
        var exclusionsByOperationId = AdminPublishedOperationSafety.Exclusions
            .ToDictionary(exclusion => exclusion.OperationId, StringComparer.Ordinal);

        var projected = catalog.Definitions
            .Where(definition => !exclusionsByOperationId.ContainsKey(definition.Descriptor.OperationId))
            .Select(definition => new AdminMcpProjectedOperation(
                definition.Descriptor.OperationId,
                definition.OpenApiOperationId,
                PublishedOperationTool.ProjectName(definition.Descriptor.OperationId),
                definition.Method,
                definition.Path))
            .OrderBy(operation => operation.OperationId, StringComparer.Ordinal)
            .ToArray();
        var excluded = AdminPublishedOperationSafety.Exclusions
            .OrderBy(exclusion => exclusion.OperationId, StringComparer.Ordinal)
            .Select(exclusion => new AdminMcpExcludedOperation(
                exclusion.OperationId,
                exclusion.OpenApiOperationId,
                exclusion.Code,
                exclusion.Reason))
            .ToArray();

        var coveredOperationIds = projected.Select(operation => operation.OperationId)
            .Concat(excluded.Select(operation => operation.OperationId))
            .ToArray();
        coveredOperationIds.Should().OnlyHaveUniqueItems();
        coveredOperationIds.Should().BeEquivalentTo(
            catalog.Definitions.Select(definition => definition.Descriptor.OperationId),
            "the artifact cannot silently omit an Admin OpenAPI operation");

        var document = new AdminMcpCoverageDocument(
            "honua.admin-mcp-coverage.v1",
            "docs/developer/api-specs/admin-api.json",
            new AdminMcpCoverageSummary(catalog.OpenApiOperationIds.Count, projected.Length, excluded.Length),
            projected,
            excluded);
        return JsonSerializer.Serialize(document, SerializerOptions) + "\n";
    }

    private static string ResolveRepositoryPath(string relativePath)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(Path.Combine(current.FullName, "Honua.sln")) || File.Exists(candidate))
            {
                return candidate;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException($"Could not locate the repository root for '{relativePath}'.");
    }

    private sealed record AdminMcpCoverageDocument(
        string SchemaVersion,
        string Source,
        AdminMcpCoverageSummary Summary,
        IReadOnlyList<AdminMcpProjectedOperation> Projected,
        IReadOnlyList<AdminMcpExcludedOperation> Excluded);

    private sealed record AdminMcpCoverageSummary(
        int OpenApiOperationCount,
        int ProjectedOperationCount,
        int ExcludedOperationCount);

    private sealed record AdminMcpProjectedOperation(
        string OperationId,
        string OpenApiOperationId,
        string ToolName,
        string Method,
        string Path);

    private sealed record AdminMcpExcludedOperation(
        string OperationId,
        string OpenApiOperationId,
        string Code,
        string Reason);
}

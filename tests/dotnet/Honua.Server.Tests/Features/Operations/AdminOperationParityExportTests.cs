// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Ai.Protocols.Mcp.Tools;
using Honua.Server.Features.Operations;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Server.Tests.ParityExports;

/// <summary>
/// Drift gate and opt-in emitter for the authoritative Admin OpenAPI and MCP
/// projection exports consumed by generated clients and candidate certification.
/// </summary>
public sealed class AdminOperationParityExportTests
{
    private const string EmitVariable = "HONUA_EMIT_ADMIN_PARITY_EXPORTS";
    private const string OpenApiExport = "admin-openapi-operation-ids.json";
    private const string McpExport = "admin-mcp-projection-manifest.json";

    [UnitTest]
    public void AdminOpenApiOperationIds_CommittedExportMatchesCurrentContract()
    {
        var expected = GenerateOpenApiExport();
        ReadCommitted(OpenApiExport).Should().Be(expected,
            "the generated CLI and Console roster must consume the exact committed Admin OpenAPI operation export");
    }

    [UnitTest]
    public void AdminMcpProjectionManifest_CommittedExportMatchesCurrentProjection()
    {
        var expected = GenerateMcpExport();
        ReadCommitted(McpExport).Should().Be(expected,
            "the MCP roster must consume the exact committed projection rather than reconstructing it independently");
    }

    [UnitTest]
    public void AdminParityExports_EmitWhenExplicitlyRequested()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable(EmitVariable), "1", StringComparison.Ordinal))
            return;

        var directory = RepositoryPaths.Resolve("docs", "gis", "data");
        File.WriteAllText(Path.Combine(directory, OpenApiExport), GenerateOpenApiExport(), new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(directory, McpExport), GenerateMcpExport(), new UTF8Encoding(false));
    }

    private static string GenerateOpenApiExport()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(
            RepositoryPaths.Resolve("docs", "developer", "api-specs", "admin-api.json")));
        var catalogIds = AdminApiOperationCatalog.Definitions.ToDictionary(
            definition => definition.OpenApiOperationId,
            definition => definition.OperationId,
            StringComparer.Ordinal);

        var operations = document.RootElement.GetProperty("paths").EnumerateObject()
            .SelectMany(path => path.Value.EnumerateObject()
                .Where(method => IsHttpMethod(method.Name) && method.Value.TryGetProperty("operationId", out _))
                .Select(method => new
                {
                    operationId = method.Value.GetProperty("operationId").GetString(),
                    catalogOperationId = catalogIds.GetValueOrDefault(
                        method.Value.GetProperty("operationId").GetString()!),
                    method = method.Name.ToUpperInvariant(),
                    path = path.Name,
                }))
            .OrderBy(item => item.operationId, StringComparer.Ordinal)
            .ToArray();

        return Serialize(new
        {
            schemaVersion = "1.0.0",
            authority = "docs/developer/api-specs/admin-api.json",
            generatedCliFields = CanonicalEnvelopeFields,
            generatedConsoleFields = CanonicalEnvelopeFields,
            operations,
        });
    }

    private static string GenerateMcpExport()
    {
        var operations = AdminApiOperationCatalog.Descriptors
            .OrderBy(descriptor => descriptor.OperationId, StringComparer.Ordinal)
            .Select(descriptor =>
            {
                var tool = new PublishedOperationTool(descriptor, "export", NullLogger.Instance).Describe();
                return new
                {
                    operationId = descriptor.OperationId,
                    toolName = tool.Name,
                    title = tool.Title,
                    inputFields = tool.InputSchema.GetProperty("properties").EnumerateObject()
                        .Select(property => property.Name).Order(StringComparer.Ordinal).ToArray(),
                    outputFields = tool.OutputSchema!.Value.GetProperty("properties").EnumerateObject()
                        .Select(property => property.Name).Order(StringComparer.Ordinal).ToArray(),
                    readOnly = tool.Annotations?.ReadOnlyHint,
                    destructive = tool.Annotations?.DestructiveHint,
                    idempotent = tool.Annotations?.IdempotentHint,
                };
            }).ToArray();

        var outputFields = operations.SelectMany(operation => operation.outputFields)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        CanonicalEnvelopeFields.Should().BeSubsetOf(outputFields,
            "every generated CLI and Console operation-envelope field must survive the MCP projection");

        return Serialize(new
        {
            schemaVersion = "1.0.0",
            authority = "AdminApiOperationCatalog -> PublishedOperationTool",
            roster = new
            {
                status = "ready",
                exports = new[]
                {
                    $"docs/gis/data/{OpenApiExport}",
                    $"docs/gis/data/{McpExport}",
                },
            },
            catalogFields = CanonicalEnvelopeFields,
            generatedCliFields = CanonicalEnvelopeFields,
            generatedConsoleFields = CanonicalEnvelopeFields,
            operations,
        });
    }

    private static readonly string[] CanonicalEnvelopeFields =
    [
        "status", "operationId", "operationInstanceId", "proposalId", "correlationId", "auditId",
        "authorizationOutcome", "policyOutcome", "jobId", "message", "details", "resourceIds", "evidenceRefs",
    ];

    private static bool IsHttpMethod(string value) => value is
        "get" or "post" or "put" or "patch" or "delete" or "options" or "head" or "trace";

    private static string ReadCommitted(string fileName) =>
        File.ReadAllText(RepositoryPaths.Resolve("docs", "gis", "data", fileName));

    private static string Serialize<T>(T value) =>
        JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }) + "\n";
}

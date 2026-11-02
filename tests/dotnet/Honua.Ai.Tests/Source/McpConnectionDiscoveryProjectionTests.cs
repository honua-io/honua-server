// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Ai.Protocols.Mcp.Tools;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Operations.Domain;
using Honua.Core.Features.Operations.Services;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Protocols.Mcp;

public sealed class McpConnectionDiscoveryProjectionTests
{
    private static readonly string[] ConnectionFields = ["id", "name", "provider", "status"];
    private static readonly string[] TableFields = ["schema", "table", "geometryColumn", "geometryType", "estimatedRows"];
    private const string Canary = "private-upstream-value";

    [UnitTheory]
    [InlineData(McpConnectionDiscoveryProjection.ConnectionsOperationId,
        """{"data":[{"connectionId":"123","name":"catalog","provider":"postgis","healthStatus":"Healthy","password":"private-upstream-value","connectionString":"private-upstream-value","encryptedConnectionString":"private-upstream-value","credentialReference":"private-upstream-value","host":"private-upstream-value","username":"private-upstream-value","extra":{"secret":"private-upstream-value"}}],"message":"private-upstream-value"}""")]
    [InlineData(McpConnectionDiscoveryProjection.TablesOperationId,
        """{"tables":[{"schema":"public","table":"parcels","geometryColumn":null,"geometryType":null,"estimatedRows":null,"columns":[{"name":"private-upstream-value"}],"password":"private-upstream-value","connectionString":"private-upstream-value"}],"secret":"private-upstream-value"}""")]
    public void CreateResult_UpstreamAddsSensitiveFields_SerializesOnlyTheAllowlist(string operationId, string response)
    {
        var result = McpConnectionDiscoveryProjection.CreateResult(operationId, Handle(operationId, response));

        result.IsError.Should().BeFalse();
        var structured = result.StructuredContent
            ?? throw new Xunit.Sdk.XunitException("Expected structured content.");
        structured.GetRawText().Should().NotContain(Canary);
        result.Content.Should().OnlyContain(block => block.Text != null && !block.Text.Contains(Canary));
        var arrayName = operationId == McpConnectionDiscoveryProjection.ConnectionsOperationId ? "data" : "tables";
        var row = structured.GetProperty(arrayName)[0];
        row.EnumerateObject().Select(property => property.Name).Should().BeEquivalentTo(
            arrayName == "data" ? ConnectionFields : TableFields);
    }

    [UnitTheory]
    [InlineData(OperationHandleStatus.Failed, "{\"message\":\"private-upstream-value\"}")]
    [InlineData(OperationHandleStatus.Denied, "{\"message\":\"private-upstream-value\"}")]
    [InlineData(OperationHandleStatus.Completed, "{\"data\":[{\"connectionId\":{\"secret\":\"private-upstream-value\"}}]}")]
    [InlineData(OperationHandleStatus.Completed, "private-upstream-value")]
    public void CreateResult_FailureOrMalformedPayload_NeverSerializesUpstreamDetails(OperationHandleStatus status, string response)
    {
        var operationId = McpConnectionDiscoveryProjection.ConnectionsOperationId;
        var result = McpConnectionDiscoveryProjection.CreateResult(operationId,
            Handle(operationId, response) with { Status = status });

        result.IsError.Should().BeTrue();
        var structured = result.StructuredContent
            ?? throw new Xunit.Sdk.XunitException("Expected structured content.");
        structured.GetRawText().Should().NotContain(Canary);
        result.Content.Should().OnlyContain(block => block.Text != null && !block.Text.Contains(Canary));
    }

    [UnitTheory]
    [InlineData(McpConnectionDiscoveryProjection.ConnectionsOperationId)]
    [InlineData(McpConnectionDiscoveryProjection.TablesOperationId)]
    public void ScopeAuthority_ConnectionDiscovery_RequiresReadAuthority(string operationId)
    {
        var request = new OperationRequest { OperationId = operationId };
        foreach (var scope in new[] { OperatorScopeCatalog.Read, OperatorScopeCatalog.Full })
            OperationScopeMapping.ExceedsScopeAuthority(request, new OperationPolicyContext
            {
                ScopeGoverned = true,
                RecognizedScopes = [scope],
            }).Should().BeFalse(scope);
        foreach (var scopes in new[] { Array.Empty<string>(), new[] { OperatorScopeCatalog.Discover }, new[] { OperatorScopeCatalog.Create } })
            OperationScopeMapping.ExceedsScopeAuthority(request, new OperationPolicyContext
            {
                ScopeGoverned = true,
                RecognizedScopes = scopes,
            }).Should().BeTrue(string.Join(',', scopes));
    }

    private static OperationHandle Handle(string operationId, string response) => new()
    {
        OperationId = operationId,
        OperationInstanceId = "test-instance",
        CorrelationId = "test-correlation",
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
        Status = OperationHandleStatus.Completed,
        Reason = Canary,
        ResourceIds = new Dictionary<string, string> { ["secret"] = Canary },
        EvidenceRefs = [Canary],
        Result = new OperationResultSummary
        {
            Summary = Canary,
            Details = new Dictionary<string, string> { ["response"] = response, ["secret"] = Canary },
        },
    };
}

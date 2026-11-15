// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Ai.Protocols.Mcp.Tools;
using Honua.Core.Features.Operations.Domain;
using Honua.Server.Features.Operations;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Server.Tests.Features.OperationsToolset;

public sealed partial class AdminOperationRequestContractTests
{
    [Theory]
    [InlineData("display", "get")]
    [InlineData("display", "set")]
    [InlineData("spatial", "get")]
    [InlineData("spatial", "set")]
    public async Task LayerConfiguration_DiscoveryAndRoute_UseCanonicalAdminOperation(string section, string action)
    {
        var id = $"admin.layer.{section}.{action}";
        var descriptor = AdminApiOperationCatalog.Descriptors.Single(item => item.OperationId == id);
        descriptor.ApprovalModel.Should().Be(action == "get" ? OperationApprovalModel.None : OperationApprovalModel.OperatorGate);
        descriptor.Policy.SideEffectClass.Should().Be(action == "get" ? OperationSideEffectClass.ReadOnly : OperationSideEffectClass.MutatesMetadata);
        descriptor.Policy.SupportsDryRun.Should().BeFalse("revision-bound previews are a separate contract");
        new AdminAuditedMcpProjection().OperationIds.Should().Contain(id);
        var tool = new PublishedOperationTool(descriptor, "contract", NullLogger.Instance).Describe();
        tool.Name.Should().Be($"honua_admin_layer_{section}_{action}");
        tool.InputSchema.GetProperty("required").EnumerateArray().Select(value => value.GetString())
            .Should().Contain("storageLayerId");

        using var capture = new CaptureHandler();
        using var client = new HttpClient(capture);
        var executor = CreateApi(id, client);
        await executor.SubmitAsync(new OperationRequest
        {
            OperationId = id,
            Parameters = new Dictionary<string, string?> { ["storageLayerId"] = "741" },
        }, new OperationPolicyContext());
        capture.Uri!.AbsolutePath.Should().Be($"/api/v1/admin/metadata/layers/741/{section}");
    }

    [Theory]
    [InlineData("display", "minScale", "125.5")]
    [InlineData("display", "defaultVisibility", "false")]
    [InlineData("display", "displayField", "2026")]
    [InlineData("spatial", "clearStorageCrs", "true")]
    [InlineData("spatial", "spatialReference", "{\"wkid\":3857}")]
    [InlineData("spatial", "supportedCrs", "[{\"wkid\":4326}]")]
    public async Task LayerConfiguration_SetBody_PreservesCanonicalTypes(string section, string field, string value)
    {
        using var capture = new CaptureHandler();
        using var client = new HttpClient(capture);
        var executor = CreateApi($"admin.layer.{section}.set", client);
        await executor.SubmitAsync(new OperationRequest
        {
            OperationId = executor.OperationId,
            Parameters = new Dictionary<string, string?> { ["storageLayerId"] = "741", [field] = value },
        }, new OperationPolicyContext());
        using var expected = JsonDocument.Parse(field == "displayField" ? JsonSerializer.Serialize(value) : value);
        using var body = JsonDocument.Parse(capture.Body!);
        body.RootElement.GetProperty(field).GetRawText().Should().BeEquivalentTo(expected.RootElement.GetRawText());
        body.RootElement.TryGetProperty("storageLayerId", out _).Should().BeFalse();
        body.RootElement.EnumerateObject().Should().ContainSingle("omitted patch fields must remain omitted");
    }
}

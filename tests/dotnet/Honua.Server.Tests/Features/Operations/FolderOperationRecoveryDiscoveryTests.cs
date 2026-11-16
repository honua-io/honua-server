// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.OperationsToolset;

public sealed partial class FolderOperationIntegrationTests
{
    [IntegrationTest]
    [Endpoint("GET /api/v1/operations/")]
    public async Task FolderOperations_Discovery_StatesWhichOperationsHaveNoSupportedRecovery()
    {
        using var response = await _proposer.GetAsync("/api/v1/operations/");
        response.EnsureSuccessStatusCode();
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var operations = body.RootElement.GetProperty("data").GetProperty("operations").EnumerateArray().ToArray();
        foreach (var operationId in new[] { "folder.list", "folder.create", "folder.update", "folder.delete", "service.move" })
        {
            var descriptor = operations.Should().ContainSingle(item => item.GetProperty("operationId").GetString() == operationId).Subject;
            descriptor.TryGetProperty("compensation", out var compensation).Should().BeTrue(operationId);
            compensation.GetProperty("availability").GetString().Should().Be(operationId == "folder.list" ? "NotApplicable" : "Unavailable");
            if (operationId != "folder.list")
            {
                compensation.GetProperty("reason").GetString().Should().NotBeNullOrWhiteSpace(operationId);
            }
        }
    }
}

// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using System.Text.Json;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Ai.Protocols.Mcp.Models;
using Microsoft.AspNetCore.Http;

namespace Honua.Server.Tests.Features.Protocols.Mcp;

/// <summary>
/// Shared helpers for MCP unit tests. Creates authenticated HTTP contexts,
/// canonical plan inputs, and JSON-element wrappers for tool arguments.
/// </summary>
internal static class McpTestFactory
{
    public static DefaultHttpContext AuthenticatedHttpContext(string user = "test-user") => new()
    {
        User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, user)], "Test"))
    };

    public static DefaultHttpContext AnonymousHttpContext() => new();

    public static McpPlanInput CreateValidPlanInput() => new()
    {
        PlanId = "plan-1",
        IntentId = "intent-1",
        Steps = new List<McpPlanStepInput>
        {
            new()
            {
                StepId = "step-1",
                Kind = nameof(AnalysisPlanStepKind.Geoprocess),
                ProcessId = "buffer"
            }
        },
        Outputs = new List<string> { nameof(ArtifactKind.FeatureLayer) }
    };

    public static JsonElement ToArguments<T>(T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
    {
        var json = JsonSerializer.Serialize(value, typeInfo);
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    public static JsonElement ParseJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}

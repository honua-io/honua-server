// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using System.Text.Json;
using Honua.Core.Features.Licensing.Abstractions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Ai.Protocols.Mcp.Models;
using Honua.TestKit.Helpers;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Protocols.Mcp;

/// <summary>
/// Shared helpers for MCP unit tests. Creates authenticated HTTP contexts,
/// canonical plan inputs, and JSON-element wrappers for tool arguments.
/// </summary>
internal static class McpTestFactory
{
    public static DefaultHttpContext AuthenticatedHttpContext(
        string user = "test-user",
        HonuaEdition edition = HonuaEdition.Pro) => new()
        {
            RequestServices = CreateRequestServices(edition),
            User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, user)], "Test"))
        };

    public static DefaultHttpContext AnonymousHttpContext(
        HonuaEdition edition = HonuaEdition.Pro) => new()
        {
            RequestServices = CreateRequestServices(edition)
        };

    /// <summary>
    /// Authenticated HTTP context whose <c>RequestServices</c> additionally
    /// registers <paramref name="configureServices"/> — for tools that resolve
    /// collaborators per-request from <c>httpContext.RequestServices</c>
    /// instead of taking them as constructor dependencies (the pattern
    /// <c>CreateMapPackageTool</c> and the Studio tools use for services
    /// registered <c>Scoped</c>, to avoid a singleton tool capturing a scoped
    /// service as a captive dependency; PR #3016 review).
    /// </summary>
    public static DefaultHttpContext AuthenticatedHttpContextWithServices(
        Action<IServiceCollection> configureServices,
        string user = "test-user",
        HonuaEdition edition = HonuaEdition.Pro) => new()
        {
            RequestServices = CreateRequestServices(edition, configureServices),
            User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, user)], "Test"))
        };

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

    private static ServiceProvider CreateRequestServices(HonuaEdition edition, Action<IServiceCollection>? configureServices = null)
    {
        var license = new TestLicenseEntitlementService(edition);
        var services = new ServiceCollection()
            .AddSingleton<ILicenseEntitlementService>(license)
            .AddSingleton<ILicenseStatusProvider>(license);
        configureServices?.Invoke(services);
        return services.BuildServiceProvider();
    }
}

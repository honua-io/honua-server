// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Reflection;
using FluentAssertions;
using Honua.Ai.AiBuilder;
using Honua.Ai.AiBuilder.Planning;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Protocols.Mcp;

/// <summary>
/// Pins which planner the <c>honua_plan_analysis</c> MCP tool resolves.
/// </summary>
/// <remarks>
/// Successor to <c>LivePlanAnalysisServiceTests</c>, which exercised the live,
/// provider-backed plan lane that ADR-0076 (honua-server#3255) retired along with
/// the <c>WorkflowGeneration</c> seam it rode on. The assertions that survive that
/// removal are the registration ones, and they now carry a stronger claim: no
/// configuration re-enables a server-side inference lane, because the registration
/// no longer consults configuration at all.
/// </remarks>
[Protocol(TestProtocols.Mcp)]
public sealed class PlanAnalysisRegistrationTests
{
    [UnitTest]
    public void AddAiBuilderPlanAnalysis_ResolvesDeterministicFixturePlanner()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAiBuilderPlanAnalysis();

        using var provider = services.BuildServiceProvider();
        var planner = provider.GetRequiredService<IPlanAnalysisService>();

        planner.Should().BeOfType<FixturePlanAnalysisService>();
        planner.Engine.Should().Be("fixture");
    }

    [UnitTest]
    public void AddAiBuilderPlanAnalysis_TakesNoConfiguration_SoNoKeyCanSelectAModelBackedPlanner()
    {
        // The former overload took an IConfiguration and selected a live,
        // provider-backed planner from WorkflowGeneration:* / PlanAnalysis:* keys.
        // ADR-0076 removed server-side inference, so the selection seam is gone
        // rather than merely defaulted off. Asserting on the signature is the
        // regression guard: a config-driven lane cannot return without this test
        // failing first.
        var overloads = typeof(AiBuilderServiceCollectionExtensions)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(method => method.Name == nameof(AiBuilderServiceCollectionExtensions.AddAiBuilderPlanAnalysis))
            .ToArray();

        overloads.Should().HaveCount(1);
        overloads[0].GetParameters()
            .Should().ContainSingle()
            .Which.ParameterType.Should().Be<IServiceCollection>();
        overloads[0].GetParameters()
            .Should().NotContain(parameter => parameter.ParameterType == typeof(IConfiguration));
    }
}

// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Reflection;
using FluentAssertions;
using Honua.Ai.AiBuilder;
using Honua.Ai.AiBuilder.Planning;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Protocols.Mcp;

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

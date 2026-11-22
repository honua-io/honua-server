// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;

namespace Honua.Server.Tests.Features.Capabilities;

public sealed partial class CapabilityManifestEndpointTests
{
    [IntegrationTest]
    [Endpoint("GET /api/v1/capabilities/manifest")]
    public async Task GetManifest_DatabaseWithH3Extension_ReportsH3Available()
    {
        var checker = Substitute.For<IH3CapabilityChecker>();
        checker.IsH3AvailableAsync(default).ReturnsForAnyArgs(true);

        var analysis = await GetAnalysisLimitsAsync(checker);

        analysis.GetProperty("h3Available").GetBoolean().Should().BeTrue();
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/capabilities/manifest")]
    public async Task GetManifest_DatabaseWithoutH3Extension_ReportsH3Unavailable()
    {
        var checker = Substitute.For<IH3CapabilityChecker>();
        checker.IsH3AvailableAsync(default).ReturnsForAnyArgs(false);

        var analysis = await GetAnalysisLimitsAsync(checker);

        analysis.GetProperty("h3Available").GetBoolean().Should().BeFalse();
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/capabilities/manifest")]
    public async Task GetManifest_UnknownH3Support_OmitsTheFlag()
    {
        var checker = Substitute.For<IH3CapabilityChecker>();
        checker.IsH3AvailableAsync(default).ReturnsForAnyArgs((bool?)null);

        var analysis = await GetAnalysisLimitsAsync(checker);

        analysis.TryGetProperty("h3Available", out _).Should().BeFalse();
        analysis.GetProperty("maxH3CellsPerQuery").GetInt32().Should().BePositive();
    }

    private static async Task<JsonElement> GetAnalysisLimitsAsync(IH3CapabilityChecker checker)
    {
        await using var fixture = CreateManifestFixture().ConfigureServices(services =>
        {
            services.RemoveAll<IH3CapabilityChecker>();
            services.AddSingleton(checker);
        });
        await fixture.InitializeAsync();
        using var client = fixture.CreateAdminClient();
        using var response = await client.GetAsync("/api/v1/capabilities/manifest");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = await ReadDocumentAsync(response);
        return document.RootElement.GetProperty("limits").GetProperty("analysis").Clone();
    }
}

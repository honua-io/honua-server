// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Capabilities;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;

namespace Honua.Server.Tests.Features.Capabilities;

public sealed partial class CapabilityManifestEndpointTests
{
    // The H3 routes answer 501 without the h3-pg extension and 503 when the probe fails, so the
    // manifest must follow the same checker instead of advertising H3 unconditionally.
    [IntegrationTest]
    [Endpoint("GET /api/v1/capabilities/manifest")]
    public async Task GetManifest_H3ExtensionInstalled_ReportsH3Available()
    {
        var h3 = await GetAdminH3CapabilityAsync(ReplaceChecker(Task.FromResult<bool?>(true)));

        h3.GetProperty("available").GetBoolean().Should().BeTrue();
        h3.TryGetProperty("reasonCode", out _).Should().BeFalse();
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/capabilities/manifest")]
    public async Task GetManifest_H3ExtensionMissing_ReportsDependencyUnavailable()
    {
        var h3 = await GetAdminH3CapabilityAsync(ReplaceChecker(Task.FromResult<bool?>(false)));

        h3.GetProperty("available").GetBoolean().Should().BeFalse();
        h3.GetProperty("reasonCode").GetString().Should().Be(CapabilityUnavailableCodes.ErrorCode);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/capabilities/manifest")]
    public async Task GetManifest_H3ProbeFailed_ReportsDependencyUnavailable()
    {
        var h3 = await GetAdminH3CapabilityAsync(ReplaceChecker(Task.FromResult<bool?>(null)));

        h3.GetProperty("available").GetBoolean().Should().BeFalse();
        h3.GetProperty("reasonCode").GetString().Should().Be(CapabilityUnavailableCodes.ErrorCode);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/capabilities/manifest")]
    public async Task GetManifest_H3AnalyticsWithoutAChecker_ReportsUnsupported()
    {
        var h3 = await GetAdminH3CapabilityAsync(static services => services.RemoveAll<IH3CapabilityChecker>());

        h3.GetProperty("available").GetBoolean().Should().BeFalse();
        h3.GetProperty("reasonCode").GetString().Should().Be("unsupported");
    }

    private static Action<IServiceCollection> ReplaceChecker(Task<bool?> probeResult)
    {
        var checker = Substitute.For<IH3CapabilityChecker>();
        checker.IsH3AvailableAsync(Arg.Any<CancellationToken>()).Returns(probeResult);
        return services =>
        {
            services.RemoveAll<IH3CapabilityChecker>();
            services.AddSingleton(checker);
        };
    }

    private static async Task<JsonElement> GetAdminH3CapabilityAsync(Action<IServiceCollection> configureServices)
    {
        await using var fixture = CreateManifestFixture().ConfigureServices(configureServices);
        await fixture.InitializeAsync();
        using var client = fixture.CreateAdminClient();
        using var response = await client.GetAsync("/api/v1/capabilities/manifest");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = await ReadDocumentAsync(response);
        return GetCapability(document.RootElement, "analytics.h3").Clone();
    }
}

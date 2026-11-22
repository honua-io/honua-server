// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Text;
using FluentAssertions;
using Honua.Core.Configuration;
using Honua.Core.Features.Licensing.Abstractions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Core.Features.Raster;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Server.Features.Protocols.Elevation;
using Honua.Server.Tests.Features.Admin;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.Elevation;

public sealed class ElevationAnalysisLogSanitizationTests
{
    [UnitTheory]
    [InlineData("sun-shadow")]
    [InlineData("slice")]
    [InlineData("line-of-sight")]
    [InlineData("viewshed")]
    public async Task MalformedJson_WithCrLfDatasetId_SanitizesDatasetIdInWarningLog(string action)
    {
        using var logCapture = new NetworkTopologyEditAdminEndpointsTests.CapturingLoggerProvider();
        await using var app = await CreateTestAppAsync(logCapture);

        using var client = app.GetTestClient();
        using var content = new StringContent("{not-json", Encoding.UTF8, "application/json");
        var response = await client.PostAsync(
            $"/elevation/bad%0d%0a%5BERR%5D%20FORGED_ID/{action}",
            content);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var parseWarnings = logCapture.Messages
            .Where(m => m.Contains("Failed to parse", StringComparison.Ordinal))
            .ToList();

        parseWarnings.Should().ContainSingle();
        parseWarnings[0].Should().NotContain("\r").And.NotContain("\n");
        parseWarnings[0].Should().Contain("bad__[ERR] FORGED_ID");
    }

    private static async Task<WebApplication> CreateTestAppAsync(ILoggerProvider logCapture)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(logCapture);
        builder.Services.Configure<LimitsOptions>(_ => { });

        var entitlementService = Substitute.For<ILicenseEntitlementService>();
        entitlementService.CheckEntitlement(Arg.Any<string>())
            .Returns(ci => new LicenseEntitlementDecision(
                ci.Arg<string>(),
                IsActive: true,
                HonuaEdition.Pro,
                LicenseValidationState.Valid,
                HonuaEdition.Pro,
                string.Empty));
        builder.Services.AddSingleton(entitlementService);

        var elevationService = Substitute.For<IElevationService>();
        builder.Services.AddSingleton(new SceneAnalysisService(elevationService));
        builder.Services.AddSingleton(new VisibilityAnalysisService(elevationService));

        var app = builder.Build();
        app.MapSceneAnalysisEndpoints();
        app.MapVisibilityAnalysisEndpoints();
        await app.StartAsync();
        return app;
    }
}

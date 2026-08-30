// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Capabilities;
using Honua.Geoprocessing;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Geoprocessing;

public sealed class GeoprocessingProblemDetailsHelpersTests
{
    [Fact]
    public async Task StoreUnavailable_WithReceipt_EmitsCanonicalCapabilityProblem()
    {
        var context = BuildContext();
        var exception = GeoprocessingStoreUnavailableException.ForCause(
            DurableJobSubstrateCause.RedisNotEntitled);

        await GeoprocessingProblemDetailsHelpers.StoreUnavailable(context, exception)
            .ExecuteAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        context.Response.Body.Position = 0;
        using var document = await JsonDocument.ParseAsync(context.Response.Body);
        var root = document.RootElement;
        root.GetProperty("type").GetString().Should().Be(CapabilityUnavailableCodes.ProblemType);
        root.GetProperty("code").GetString().Should().Be(CapabilityUnavailableCodes.EntitlementErrorCode);
        root.GetProperty("capability").GetString().Should().Be(CapabilityUnavailableCodes.DurableJobsCapability);
        root.GetProperty("missingEntitlement").GetString().Should().Be(CapabilityUnavailableCodes.RedisCacheEntitlement);
        root.TryGetProperty("missingDependency", out _).Should().BeFalse();
        root.GetProperty("remediation").GetString().Should().Be(CapabilityUnavailableCodes.EntitlementRemediation);
    }

    private static DefaultHttpContext BuildContext()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        return new DefaultHttpContext
        {
            RequestServices = services.BuildServiceProvider(),
            Request = { Path = "/api/v1/analysis/reports/job-1" },
            Response = { Body = new MemoryStream() },
        };
    }
}

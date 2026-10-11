// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using FluentAssertions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Infrastructure.Health;
using Honua.Infrastructure.Monitoring;
using Honua.TestKit;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;

namespace Honua.Server.Tests.Features.Infrastructure.Monitoring;

/// <summary>
/// Boots the real server composition with a compatibility checker that reports the database as
/// transiently unavailable (SQLSTATE 53300, as on the aws-serverless certification cell in run
/// 38133126673) or as incompatible. A transient result must start the host degraded and not ready;
/// an incompatible database must still abort startup.
/// </summary>
public sealed class DatabasePreflightDegradedStartTests
{
    private const string SlotsExhaustedClass = "SQLSTATE 53300 (class 53: insufficient resources)";

    [Fact]
    [Trait("Tier", "Integration")]
    public async Task Startup_WhenPreflightTransientAndDegradedStartEnabled_StartsNotReadyWithTypedReason()
    {
        await using var baseFactory = new TestWebApplicationFactory();
        await using var factory = WithPreflight(
            baseFactory,
            DatabaseCompatibilityResult.Unavailable(SlotsExhaustedClass),
            degradedStart: "true");

        using var client = factory.CreateClient();

        var context = factory.Services.GetRequiredService<DegradedStartupContext>();
        context.IsAwaitingDatabase.Should().BeTrue();
        context.Reason.Should().Be("PostGIS preflight: " + SlotsExhaustedClass);
        factory.Services.GetRequiredService<DatabaseCompatibilityState>().Result!.IsTransientFailure.Should().BeTrue();

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var readiness = await scope.ServiceProvider.GetRequiredService<IReadinessCheckService>()
                .CheckReadinessAsync();
            readiness.IsReady.Should().BeFalse();
            readiness.ReasonCode.Should().Be(ReadinessReasonCodes.DatabaseStartupDegraded);
            readiness.Message.Should().Contain("53300").And.NotContain("Password").And.NotContain("Host=");
        }

        using var response = await client.GetAsync(new Uri("/healthz/ready", UriKind.Relative));
        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        response.Headers.GetValues(ReadinessReasonCodes.HeaderName)
            .Should().ContainSingle().Which.Should().Be(ReadinessReasonCodes.DatabaseStartupDegraded);

        using var live = await client.GetAsync(new Uri("/healthz/live", UriKind.Relative));
        live.StatusCode.Should().Be(HttpStatusCode.OK, "a degraded host still answers liveness");
    }

    [Fact]
    [Trait("Tier", "Integration")]
    public async Task Startup_OnAwsLambdaWithDegradedStartUnset_StartsDegradedInsteadOfExiting()
    {
        await using var baseFactory = new TestWebApplicationFactory();
        await using var factory = WithPreflight(
            baseFactory,
            DatabaseCompatibilityResult.Unavailable(SlotsExhaustedClass),
            degradedStart: null,
            lambdaFunctionName: "honua-api");

        using var client = factory.CreateClient();

        factory.Services.GetRequiredService<DegradedStartupContext>().IsAwaitingDatabase.Should().BeTrue();
    }

    [Fact]
    [Trait("Tier", "Integration")]
    public void Startup_WhenPreflightTransientAndDegradedStartDisabled_FailsWithSanitizedReason()
    {
        using var baseFactory = new TestWebApplicationFactory();
        using var factory = WithPreflight(
            baseFactory,
            DatabaseCompatibilityResult.Unavailable(SlotsExhaustedClass),
            degradedStart: "false");

        var start = () => factory.CreateClient();

        start.Should().Throw<InvalidOperationException>()
            .WithMessage("PostGIS preflight check failed: Database unavailable*SQLSTATE 53300*");
    }

    [Fact]
    [Trait("Tier", "Integration")]
    public void Startup_WhenDatabaseIncompatible_StillFailsFastWithDegradedStartEnabled()
    {
        using var baseFactory = new TestWebApplicationFactory();
        using var factory = WithPreflight(
            baseFactory,
            DatabaseCompatibilityResult.Incompatible(
                "PostGIS extension is not installed. Honua requires PostGIS for spatial operations.",
                "PostgreSQL 15.17",
                ["plpgsql"]),
            degradedStart: "true",
            lambdaFunctionName: "honua-api");

        var start = () => factory.CreateClient();

        start.Should().Throw<InvalidOperationException>()
            .WithMessage("PostGIS preflight check failed: PostGIS extension is not installed*");
    }

    private static WebApplicationFactory<Program> WithPreflight(
        TestWebApplicationFactory baseFactory,
        DatabaseCompatibilityResult preflightResult,
        string? degradedStart,
        string? lambdaFunctionName = null)
        => baseFactory.WithWebHostBuilder(builder =>
        {
            // Program reads the degraded-start switch while composing services, before ordinary
            // app-configuration callbacks run, so these must be host settings.
            if (degradedStart is not null)
            {
                builder.UseSetting("Database:StartupResilience:DegradedStartEnabled", degradedStart);
            }

            if (lambdaFunctionName is not null)
            {
                builder.UseSetting("AWS_LAMBDA_FUNCTION_NAME", lambdaFunctionName);
            }

            builder.ConfigureTestServices(services =>
            {
                var checker = Substitute.For<IDatabaseCompatibilityChecker>();
                checker.CheckCompatibilityAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                    .Returns(preflightResult);
                services.RemoveAll<IDatabaseCompatibilityChecker>();
                services.AddSingleton(checker);
            });
        });
}

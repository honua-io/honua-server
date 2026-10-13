// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Infrastructure.Health;
using Honua.Infrastructure.Monitoring;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Infrastructure.Monitoring;

public sealed class DegradedStartupMutationFenceTests
{
    [UnitTheory]
    [InlineData(null, ReadinessReasonCodes.DatabaseStartupDegraded)]
    [InlineData(ReadinessReasonCodes.SchemaFloor, ReadinessReasonCodes.SchemaFloor)]
    public async Task ApplyEdits_WhileAwaitingDatabase_Returns503AndDoesNotRun(string? reasonCode, string expectedReason)
    {
        var executed = false;
        var context = Context(
            HttpMethods.Post,
            "/rest/services/parcels/FeatureServer/0/applyEdits",
            Awaiting(reasonCode));

        await InvokeAsync(context, () => executed = true);

        executed.Should().BeFalse();
        context.Response.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        context.Response.Headers[ReadinessReasonCodes.HeaderName].ToString().Should().Be(expectedReason);
    }

    [UnitTheory]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    public async Task MutatingMethod_WhileAwaitingDatabase_Returns503AndDoesNotRun(string method)
    {
        var executed = false;
        var context = Context(method, "/rest/services/parcels/FeatureServer/0/applyEdits", Awaiting(null));

        await InvokeAsync(context, () => executed = true);

        executed.Should().BeFalse();
        context.Response.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        context.Response.Headers[ReadinessReasonCodes.HeaderName].ToString()
            .Should().Be(ReadinessReasonCodes.DatabaseStartupDegraded);
    }

    [UnitTheory]
    [InlineData("GET")]
    [InlineData("HEAD")]
    public async Task Read_WhileAwaitingDatabase_IsNotRefused(string method)
    {
        var executed = false;
        var context = Context(method, "/rest/services/parcels/FeatureServer/0/query", Awaiting(ReadinessReasonCodes.SchemaFloor));

        await InvokeAsync(context, () => executed = true);

        executed.Should().BeTrue();
        context.Response.StatusCode.Should().Be(StatusCodes.Status204NoContent);
        context.Response.Headers.ContainsKey(ReadinessReasonCodes.HeaderName).Should().BeFalse();
    }

    [UnitTest]
    public async Task HealthzPost_WhileAwaitingDatabase_IsNotRefused()
    {
        var executed = false;
        var context = Context(HttpMethods.Post, "/healthz/ready", Awaiting(ReadinessReasonCodes.SchemaFloor));

        await InvokeAsync(context, () => executed = true);

        executed.Should().BeTrue();
        context.Response.StatusCode.Should().Be(StatusCodes.Status204NoContent);
    }

    [UnitTest]
    public async Task ApplyEdits_WhenDatabaseIsReady_IsNotRefused()
    {
        var executed = false;
        var context = Context(
            HttpMethods.Post,
            "/rest/services/parcels/FeatureServer/0/applyEdits",
            new DegradedStartupContext());

        await InvokeAsync(context, () => executed = true);

        executed.Should().BeTrue();
        context.Response.StatusCode.Should().Be(StatusCodes.Status204NoContent);
        context.Response.Headers.ContainsKey(ReadinessReasonCodes.HeaderName).Should().BeFalse();
    }

    private static DegradedStartupContext Awaiting(string? reasonCode)
    {
        var degraded = new DegradedStartupContext();
        degraded.MarkDegraded(
            migrationsPending: false,
            migrationsAssembly: null,
            reason: "schema behind this build",
            reasonCode: reasonCode);
        return degraded;
    }

    private static DefaultHttpContext Context(string method, string path, DegradedStartupContext degraded)
    {
        var context = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddSingleton(degraded).BuildServiceProvider()
        };
        context.Request.Method = method;
        context.Request.Path = path;
        return context;
    }

    private static Task InvokeAsync(HttpContext context, Action onNext)
    {
        var middleware = new DegradedStartupMutationFenceMiddleware(ctx =>
        {
            onNext();
            ctx.Response.StatusCode = StatusCodes.Status204NoContent;
            return Task.CompletedTask;
        });
        return middleware.InvokeAsync(context);
    }
}

// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.ControlPlane;
using Honua.Core.Configuration;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Db.Postgres.Features.Infrastructure;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

/// <summary>
/// The control-plane event-handler Lambdas (reconcile, backstop, scheduled tick) release their
/// pooled database connections at the end of every invocation so a frozen instance holds none
/// (honua-release e2e-cloud-aws run 38125145738 exhausted the cell's RDS from the tick Lambda).
/// </summary>
public sealed class ControlPlaneLambdaConnectionReleaseTests
{
    private static IConfiguration Configuration(string? handler = null, string? option = null)
    {
        var values = new Dictionary<string, string?>();
        if (handler is not null)
        {
            values[ControlPlaneLambdaConnectionRelease.HandlerSelectorKey] = handler;
        }

        if (option is not null)
        {
            values[ControlPlaneLambdaConnectionRelease.OptionKey] = option;
        }

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    [UnitTest]
    public void IsEnabled_ApiHost_WithoutHandlerSelector_IsOff()
        => ControlPlaneLambdaConnectionRelease.IsEnabled(Configuration()).Should().BeFalse();

    [UnitTest]
    public void IsEnabled_EventHandlerProfile_DefaultsOn_ForEveryHandler()
    {
        foreach (var handler in new[] { "batch-event", "backstop", "scheduled-tick" })
        {
            ControlPlaneLambdaConnectionRelease.IsEnabled(Configuration(handler)).Should().BeTrue(handler);
        }
    }

    [UnitTest]
    public void IsEnabled_ExplicitFalse_OverridesEventHandlerProfile()
        => ControlPlaneLambdaConnectionRelease.IsEnabled(Configuration("scheduled-tick", "false")).Should().BeFalse();

    [UnitTest]
    public void IsEnabled_ExplicitTrue_EnablesWithoutHandlerSelector()
        => ControlPlaneLambdaConnectionRelease.IsEnabled(Configuration(option: "True")).Should().BeTrue();

    [UnitTest]
    public void IsEnabled_UnparsableValue_FailsFast()
    {
        var act = () => ControlPlaneLambdaConnectionRelease.IsEnabled(Configuration("backstop", "yes"));

        act.Should().Throw<InvalidOperationException>()
            .WithMessage($"*{ControlPlaneLambdaConnectionRelease.OptionKey}*");
    }

    [UnitTest]
    public async Task Middleware_ReleasesOncePerInvocation()
    {
        var releaser = new CountingReleaser();
        var middleware = CreateMiddleware(_ => Task.CompletedTask, releaser);

        await middleware.InvokeAsync(new DefaultHttpContext());
        releaser.Calls.Should().Be(1);

        await middleware.InvokeAsync(new DefaultHttpContext());
        releaser.Calls.Should().Be(2);
    }

    [UnitTest]
    public async Task Middleware_WhenTheTickThrows_StillReleasesOnce_AndRethrows()
    {
        var releaser = new CountingReleaser();
        var middleware = CreateMiddleware(
            _ => throw new InvalidOperationException("tick failed"),
            releaser);

        var act = () => middleware.InvokeAsync(new DefaultHttpContext());

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("tick failed");
        releaser.Calls.Should().Be(1);
    }

    [UnitTest]
    public async Task Middleware_WhenTheRequestIsAborted_StillReleases_WithAnUncancelledToken()
    {
        var releaser = new CountingReleaser();
        var middleware = CreateMiddleware(_ => throw new OperationCanceledException(), releaser);
        using var aborted = new CancellationTokenSource();
        await aborted.CancelAsync();
        var context = new DefaultHttpContext { RequestAborted = aborted.Token };

        var act = () => middleware.InvokeAsync(context);

        await act.Should().ThrowAsync<OperationCanceledException>();
        releaser.Calls.Should().Be(1);
        releaser.LastTokenWasCancelable.Should().BeFalse();
    }

    [UnitTest]
    public async Task Middleware_WhenReleaseFails_KeepsTheInvocationOutcome_AndStillRunsOtherReleasers()
    {
        var failing = new CountingReleaser { Failure = new InvalidOperationException("pool gone") };
        var second = new CountingReleaser();
        var middleware = CreateMiddleware(
            context =>
            {
                context.Response.StatusCode = StatusCodes.Status202Accepted;
                return Task.CompletedTask;
            },
            failing,
            second);
        var httpContext = new DefaultHttpContext();

        await middleware.InvokeAsync(httpContext);

        httpContext.Response.StatusCode.Should().Be(StatusCodes.Status202Accepted);
        failing.Calls.Should().Be(1);
        second.Calls.Should().Be(1);
    }

    [UnitTest]
    public async Task Pipeline_EventHandlerProfile_ReleasesAfterEachRequest()
    {
        var releaser = new CountingReleaser();
        var app = BuildPipeline(Configuration("scheduled-tick"), releaser);

        await app(new DefaultHttpContext());

        releaser.Calls.Should().Be(1);
    }

    [UnitTest]
    public async Task Pipeline_ApiHost_DoesNotRelease()
    {
        var releaser = new CountingReleaser();
        var app = BuildPipeline(Configuration(), releaser);

        await app(new DefaultHttpContext());

        releaser.Calls.Should().Be(0);
    }

    [UnitTest]
    public async Task Pipeline_OptionOff_DoesNotRelease_EvenForAnEventHandler()
    {
        var releaser = new CountingReleaser();
        var app = BuildPipeline(Configuration("backstop", "false"), releaser);

        await app(new DefaultHttpContext());

        releaser.Calls.Should().Be(0);
    }

    [UnitTest]
    public async Task PostgresReleaser_ClearsFactoryBuiltDataSources_AndSkipsDisposedOnes()
    {
        // No server is needed: clearing a pool only closes idle connections it already holds.
        var limits = new ConnectionLimits();
        await using var live = PostgresDataSourceFactory.Create(
            "Host=127.0.0.1;Port=1;Database=release_test;Username=u;Password=p", schemaHeadersEnabled: false, limits);
        var disposed = PostgresDataSourceFactory.CreateForBoundSource(
            "Host=127.0.0.1;Port=1;Database=release_test_disposed;Username=u;Password=p", limits);
        await disposed.DisposeAsync();

        PostgresDataSourceFactory.ReleasePooledConnections().Should().BeGreaterThanOrEqualTo(1);

        var act = async () => await new PostgresDatabaseConnectionReleaser().ReleasePooledConnectionsAsync();
        await act.Should().NotThrowAsync();
        GC.KeepAlive(live);
    }

    private static ControlPlaneLambdaConnectionReleaseMiddleware CreateMiddleware(
        RequestDelegate next,
        params IDatabaseConnectionReleaser[] releasers)
        => new(next, releasers, NullLogger<ControlPlaneLambdaConnectionReleaseMiddleware>.Instance);

    private static RequestDelegate BuildPipeline(IConfiguration configuration, IDatabaseConnectionReleaser releaser)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
        services.AddSingleton(releaser);
        var app = new ApplicationBuilder(services.BuildServiceProvider());
        app.UseControlPlaneLambdaConnectionRelease(configuration);
        app.Run(_ => Task.CompletedTask);
        return app.Build();
    }

    private sealed class CountingReleaser : IDatabaseConnectionReleaser
    {
        public int Calls { get; private set; }

        public bool LastTokenWasCancelable { get; private set; }

        public Exception? Failure { get; init; }

        public ValueTask ReleasePooledConnectionsAsync(CancellationToken cancellationToken = default)
        {
            Calls++;
            LastTokenWasCancelable = cancellationToken.CanBeCanceled;
            return Failure is null ? ValueTask.CompletedTask : ValueTask.FromException(Failure);
        }
    }
}

// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.ControlPlane;
using Honua.Core.Features.AuditLog;
using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.Guardrails.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

/// <summary>
/// Regression coverage for honua-server#1908: the durable control-plane gateway
/// graph must build under DI scope validation without a captive-dependency error.
/// </summary>
/// <remarks>
/// <para>
/// <c>OperationGateway</c> is registered as a <b>singleton</b> (the production
/// composition root in <c>Program.cs</c> wires it that way on the Pro+Redis durable
/// path), but it writes audit events through
/// <see cref="IAuditLog"/>, which the production registration
/// (<c>AddHonuaAuditLog</c>) wires as <b>scoped</b> — the Postgres sink needs a
/// per-operation DB connection. A singleton that takes a scoped service as a
/// constructor dependency is a captive dependency: ASP.NET Core's
/// <c>ServiceProviderOptions.ValidateScopes</c>/<c>ValidateOnBuild</c> (both ON in
/// Development) reject it at <c>Build()</c>, so the server crash-loops on startup
/// (container exit 139).
/// </para>
/// <para>
/// This only surfaced on the durable control-plane path because the singleton
/// gateway is registered only when the Pro dev grant + a Redis connection are present
/// (the #1841 GP-job topology); the Community/no-Redis composition never registers
/// the gateway, so scope validation never saw the captive dependency.
/// </para>
/// <para>
/// This test reproduces the crash in isolation, mirroring
/// <c>MetadataReleaseLifecycleFixTests</c> (the established pattern in this folder for
/// the same class of scoped-from-singleton bug): it registers the <b>real</b>
/// <c>OperationGateway</c> as a singleton with <see cref="IAuditLog"/> registered
/// <b>scoped</b> exactly as production does, then builds the provider with scope
/// validation ON. Before the fix (gateway captures <c>IAuditLog</c> in its
/// constructor) <c>BuildServiceProvider</c> throws; after the fix (gateway resolves
/// <c>IAuditLog</c> per-operation via <see cref="IServiceScopeFactory"/>) it builds
/// and the singleton resolves cleanly.
/// </para>
/// </remarks>
public sealed class OperationGatewayCompositionRootTests
{
    [Fact]
    public void OperationGateway_RegisteredSingletonWithScopedAuditLogUnderScopeValidation_BuildsWithoutCaptiveDependency()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        // IAuditLog is scoped in production (AddHonuaAuditLog -> TryAddScoped). This is
        // the scoped service that a captive singleton would illegally capture.
        services.AddScoped<IAuditLog>(_ => NullAuditLog.Instance);

        // The gateway's other collaborators are singleton-safe in production; their
        // behaviour is irrelevant to the captive-dependency check, only their presence
        // and lifetime in the constructor graph that ValidateOnBuild walks.
        services.AddSingleton(Substitute.For<IGuardrailLadder>());
        services.AddSingleton(Substitute.For<IOperationProposalStore>());
        services.AddSingleton(Substitute.For<IProposalNotifier>());

        // The real production registration: OperationGateway as a singleton (mirrors
        // Program.cs AddSingleton<IOperationGateway, OperationGateway>()).
        services.AddSingleton<IOperationGateway, OperationGateway>();

        // ValidateScopes + ValidateOnBuild reproduce the Development/production default
        // that surfaced #1908: the provider walks every singleton's constructor graph at
        // build time and fails closed if a singleton consumes a scoped service.
        var build = () => services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true,
        });

        using var provider = build.Should().NotThrow(
            "the singleton OperationGateway must not capture the scoped IAuditLog; it " +
            "resolves IAuditLog per-operation via IServiceScopeFactory (honua-server#1908)")
            .Subject;

        // The singleton must resolve from the ROOT provider (as the production host does)
        // without a scope, proving no scoped service is captured in its constructor.
        provider.GetRequiredService<IOperationGateway>().Should().NotBeNull();
    }
}

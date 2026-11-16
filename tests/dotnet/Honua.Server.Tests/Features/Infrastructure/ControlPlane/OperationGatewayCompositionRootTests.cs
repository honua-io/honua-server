// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

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

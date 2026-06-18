// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Studio;
using Honua.Core.Features.Studio.Abstractions;
using Honua.Server.Features.Studio.Export;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Honua.Server.Tests.Features.Studio;

/// <summary>
/// Guards the DI lifetime of <see cref="IStudioDeliverableExporter"/>. The exporter captures the
/// scoped <see cref="IStudioPackageLifecycleService"/>, so a singleton registration is a captive
/// dependency that fails host-build scope validation (regression guard for PR #1754).
/// </summary>
[Protocol(TestProtocols.Studio)]
[Operation(Operations.StudioLifecycle)]
public sealed class StudioDeliverableExporterRegistrationTests
{
    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Tier", "Fast")]
    public void Exporter_IsRegisteredScoped_NotCapturingScopedLifecycleAsSingleton()
    {
        var services = new ServiceCollection();
        services.AddStudioPackageLifecycle();

        // Mirror the production registration in FeatureRegistrationExtensions.
        services.TryAddScoped<IStudioDeliverableExporter, StudioDeliverableExporter>();

        var exporter = services.Single(d => d.ServiceType == typeof(IStudioDeliverableExporter));
        var lifecycle = services.Single(d => d.ServiceType == typeof(IStudioPackageLifecycleService));

        // The exporter must never outlive its scoped dependency.
        exporter.Lifetime.Should().Be(ServiceLifetime.Scoped);
        lifecycle.Lifetime.Should().Be(ServiceLifetime.Scoped);
        exporter.Lifetime.Should().NotBe(
            ServiceLifetime.Singleton,
            "a singleton exporter would capture the scoped IStudioPackageLifecycleService (captive dependency)");
    }
}

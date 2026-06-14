// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.AuditLog;
using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Licensing.Abstractions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Plugins.Abstractions;
using Honua.TestKit.Helpers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Honua.Plugins.Tests;

public sealed class PluginManifestTests
{
    private static ServiceProvider Build(Action<IHonuaPluginBuilder> configure)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ILicenseEntitlementService>(new TestLicenseEntitlementService(HonuaEdition.Enterprise));
        services.AddSingleton<IAuditLog, NullAuditLog>();
        services.AddHonuaPlugins(new ConfigurationBuilder().Build(), configure);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void Manifest_ParsesCapabilitiesAndDependencies()
    {
        var manifest = PluginManifest.FromAttribute(new PluginAttribute("p", "2.3.4")
        {
            Capabilities = PluginCapability.BackgroundExecution | PluginCapability.CustomEndpoints,
            DependsOnCsv = "a@>=1.0.0; b",
            MinimumServerVersion = "1.5.0",
        });

        manifest.Version.Should().Be(new Version(2, 3, 4));
        manifest.Capabilities.Should().HaveFlag(PluginCapability.BackgroundExecution);
        manifest.Dependencies.Should().HaveCount(2);
        manifest.Dependencies[0].Id.Should().Be("a");
        manifest.Dependencies[0].MinimumVersion.Should().Be(new Version(1, 0, 0));
        manifest.Dependencies[1].Id.Should().Be("b");
        manifest.Dependencies[1].MinimumVersion.Should().BeNull();
        manifest.MinimumServerVersion.Should().Be(new Version(1, 5, 0));
    }

    [Fact]
    public void Manifest_StripsPreReleaseSuffix()
    {
        PluginManifest.FromAttribute(new PluginAttribute("p", "1.2.0-rc.1")).Version
            .Should().Be(new Version(1, 2, 0));
    }

    [Fact]
    public void Manifest_Throws_OnInvalidVersion()
    {
        var act = () => PluginManifest.FromAttribute(new PluginAttribute("p", "not-a-version"));
        act.Should().Throw<InvalidOperationException>().WithMessage("*invalid version*");
    }

    [Fact]
    public void Register_Throws_WhenDependencyMissing()
    {
        var act = () => Build(p => p.Add<DependentPlugin>());
        act.Should().Throw<InvalidOperationException>().WithMessage("*not registered*");
    }

    [Fact]
    public void Register_Throws_WhenDependencyVersionTooLow()
    {
        var act = () => Build(p => p.Add<OldDependency>().Add<DependentPlugin>());
        act.Should().Throw<InvalidOperationException>().WithMessage("*requires*>=*");
    }

    [Fact]
    public void Register_Succeeds_WhenDependencySatisfied()
    {
        using var provider = Build(p => p.Add<GoodDependency>().Add<DependentPlugin>());
        provider.GetRequiredService<PluginCatalog>().Plugins.Should().HaveCount(2);
    }

    [Fact]
    public void Register_Throws_WhenBackgroundServiceLacksCapability()
    {
        var act = () => Build(p => p.Add<UncapabledBackgroundPlugin>());
        act.Should().Throw<InvalidOperationException>().WithMessage("*BackgroundExecution capability*");
    }

    [Fact]
    public void Register_Succeeds_WhenBackgroundServiceDeclaresCapability()
    {
        using var provider = Build(p => p.Add<CapabledBackgroundPlugin>());
        provider.GetRequiredService<PluginCatalog>().Plugins.Single().Manifest.Capabilities
            .Should().HaveFlag(PluginCapability.BackgroundExecution);
    }

    [Fact]
    public void Catalog_ReportsExtensionPointFlags()
    {
        using var provider = Build(p => p.Add<MultiPointPlugin>());
        var registration = provider.GetRequiredService<PluginCatalog>().Plugins.Single();

        registration.ProvidesFieldValidator.Should().BeTrue();
        registration.ProvidesComputedField.Should().BeTrue();
        registration.ProvidesValidator.Should().BeFalse();
    }

    [Plugin("dependent", "1.0.0", DependsOnCsv = "base-dep@>=2.0.0")]
    private sealed class DependentPlugin : IFeatureValidator
    {
        public ValueTask<PluginValidationResult> ValidateAsync(Feature feature, PluginValidationContext context, CancellationToken cancellationToken)
            => ValueTask.FromResult(PluginValidationResult.Success());
    }

    [Plugin("base-dep", "1.0.0")]
    private sealed class OldDependency : IFeatureValidator
    {
        public ValueTask<PluginValidationResult> ValidateAsync(Feature feature, PluginValidationContext context, CancellationToken cancellationToken)
            => ValueTask.FromResult(PluginValidationResult.Success());
    }

    [Plugin("base-dep", "2.1.0")]
    private sealed class GoodDependency : IFeatureValidator
    {
        public ValueTask<PluginValidationResult> ValidateAsync(Feature feature, PluginValidationContext context, CancellationToken cancellationToken)
            => ValueTask.FromResult(PluginValidationResult.Success());
    }

    [Plugin("uncapabled-bg", "1.0.0")]
    private sealed class UncapabledBackgroundPlugin : IPluginBackgroundService
    {
        public Task ExecuteAsync(CancellationToken stoppingToken) => Task.CompletedTask;
    }

    [Plugin("capabled-bg", "1.0.0", Capabilities = PluginCapability.BackgroundExecution)]
    private sealed class CapabledBackgroundPlugin : IPluginBackgroundService
    {
        public Task ExecuteAsync(CancellationToken stoppingToken) => Task.CompletedTask;
    }

    [Plugin("multi-point", "1.0.0")]
    private sealed class MultiPointPlugin : IFieldValidator, IComputedFieldProvider
    {
        string IFieldValidator.FieldName => "F";

        string IComputedFieldProvider.FieldName => "G";

        public ValueTask<PluginValidationResult> ValidateFieldAsync(object? value, Feature feature, PluginValidationContext context, CancellationToken cancellationToken)
            => ValueTask.FromResult(PluginValidationResult.Success());

        public ValueTask<object?> ComputeAsync(Feature feature, ComputedFieldContext context, CancellationToken cancellationToken)
            => ValueTask.FromResult<object?>(null);
    }
}

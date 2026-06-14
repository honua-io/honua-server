// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.AuditLog;
using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Core.Features.Licensing.Abstractions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Plugins.Abstractions;
using Honua.Sample.UtilityValidationPlugin;
using Honua.TestKit.Helpers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Honua.Plugins.Tests;

public sealed class AddHonuaPluginsTests
{
    private static ServiceProvider BuildProvider(
        HonuaEdition edition,
        Action<IHonuaPluginBuilder>? configure)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ILicenseEntitlementService>(new TestLicenseEntitlementService(edition));
        services.AddSingleton<IAuditLog, NullAuditLog>();
        services.AddHonuaPlugins(new ConfigurationBuilder().Build(), configure);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void RegistersPipelineAndCatalog_WithSamplePlugin()
    {
        using var provider = BuildProvider(HonuaEdition.Enterprise, p => p.Add<UtilityValidationPlugin>());

        var pipeline = provider.GetRequiredService<IPluginEditPipeline>();
        pipeline.HasPlugins.Should().BeTrue();

        var catalog = provider.GetRequiredService<PluginCatalog>();
        catalog.Plugins.Should().ContainSingle()
            .Which.Id.Should().Be("utility-validation");

        provider.GetServices<IFeatureValidator>().Should().ContainSingle();
    }

    [Fact]
    public void RegistersNoOpPipeline_WhenNoPlugins()
    {
        using var provider = BuildProvider(HonuaEdition.Enterprise, configure: null);

        provider.GetRequiredService<IPluginEditPipeline>().HasPlugins.Should().BeFalse();
        provider.GetRequiredService<PluginCatalog>().Plugins.Should().BeEmpty();
    }

    [Fact]
    public void SharesSingleInstance_WhenPluginIsValidatorAndHook()
    {
        using var provider = BuildProvider(HonuaEdition.Enterprise, p => p.Add<ValidatorAndHookPlugin>());

        var asValidator = provider.GetRequiredService<IFeatureValidator>();
        var asHook = provider.GetRequiredService<IEditHook>();
        asValidator.Should().BeSameAs(asHook);
    }

    [Fact]
    public void Add_Throws_WhenTypeMissingPluginAttribute()
    {
        var act = () => BuildProvider(HonuaEdition.Enterprise, p => p.Add<NotAPlugin>());
        act.Should().Throw<InvalidOperationException>().WithMessage("*[Plugin*");
    }

    [Fact]
    public void Add_Throws_WhenPluginImplementsNoExtensionPoint()
    {
        var act = () => BuildProvider(HonuaEdition.Enterprise, p => p.Add<EmptyPlugin>());
        act.Should().Throw<InvalidOperationException>().WithMessage("*IFeatureValidator*");
    }

    [Fact]
    public void Add_Throws_OnDuplicatePluginId()
    {
        var act = () => BuildProvider(HonuaEdition.Enterprise, p => p
            .Add<UtilityValidationPlugin>()
            .Add<UtilityValidationPlugin>());
        act.Should().Throw<InvalidOperationException>().WithMessage("*already registered*");
    }

    [Fact]
    public void Pipeline_DoesNotCaptureScopedDependencies_UnderValidateScopes()
    {
        // Regression (#347): IAuditLog is registered scoped by the server (PostgresAuditLog needs
        // a per-request connection). The pipeline must therefore not be a singleton, or it becomes
        // a captive dependency that fails ValidateScopes/ValidateOnBuild — which ASP.NET enables in
        // Development, breaking unrelated host-startup tests.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ILicenseEntitlementService>(new TestLicenseEntitlementService(HonuaEdition.Enterprise));
        services.AddScoped<IAuditLog>(_ => new RecordingAuditLog()); // mirrors the server's scoped registration
        services.AddHonuaPlugins(new ConfigurationBuilder().Build(), p => p.Add<UtilityValidationPlugin>());

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });

        using var scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<IPluginEditPipeline>().HasPlugins.Should().BeTrue();
    }

    [Fact]
    public void PluginSdk_EntitlementIsEnterprise()
    {
        var feature = FeatureCatalog.All.SingleOrDefault(f => f.Key == FeatureCatalog.PluginSdkKey);

        feature.Should().NotBeNull();
        feature!.MinimumEdition.Should().Be(HonuaEdition.Enterprise);
        feature.Category.Should().Be(FeatureCatalog.Categories.Extensibility);
    }

    [Plugin("validator-and-hook", "1.0.0")]
    private sealed class ValidatorAndHookPlugin : IFeatureValidator, IEditHook
    {
        public ValueTask<PluginValidationResult> ValidateAsync(
            Honua.Core.Features.FeatureStore.Domain.Feature feature, PluginValidationContext context, CancellationToken cancellationToken)
            => ValueTask.FromResult(PluginValidationResult.Success());

        public ValueTask<EditHookResult> OnBeforeEditAsync(EditHookContext context, CancellationToken cancellationToken)
            => ValueTask.FromResult(EditHookResult.Continue());

        public ValueTask OnAfterEditAsync(EditHookContext context, CancellationToken cancellationToken)
            => ValueTask.CompletedTask;
    }

    private sealed class NotAPlugin : IFeatureValidator
    {
        public ValueTask<PluginValidationResult> ValidateAsync(
            Honua.Core.Features.FeatureStore.Domain.Feature feature, PluginValidationContext context, CancellationToken cancellationToken)
            => ValueTask.FromResult(PluginValidationResult.Success());
    }

    [Plugin("empty", "1.0.0")]
    private sealed class EmptyPlugin
    {
    }
}

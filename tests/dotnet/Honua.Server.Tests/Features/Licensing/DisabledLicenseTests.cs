// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Licensing.Abstractions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Infrastructure.Licensing;
using Honua.Server.Startup;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using StackExchange.Redis;

namespace Honua.Server.Tests.Features.Licensing;

public sealed class DisabledLicenseTests
{
    [Theory]
    [InlineData(null, LicenseMode.Enabled)]
    [InlineData("Enabled", LicenseMode.Enabled)]
    [InlineData("enabled", LicenseMode.Enabled)]
    [InlineData("Disabled", LicenseMode.Disabled)]
    [InlineData("dIsAbLeD", LicenseMode.Disabled)]
    [InlineData(" Disabled ", LicenseMode.Disabled)]
    [Trait("Tier", "Fast")]
    public void ParseMode_SupportedValues_ReturnExpectedMode(string? value, LicenseMode expected)
        => Assert.Equal(expected, LicenseOptions.ParseMode(value));

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("Disable")]
    [InlineData("0")]
    [InlineData("1")]
    [InlineData("99")]
    [Trait("Tier", "Fast")]
    public async Task InvalidMode_BootstrapAndRegistration_RefuseStartup(string value)
    {
        var configuration = Configuration(value);
        var environment = Substitute.For<IHostEnvironment>();
        environment.EnvironmentName.Returns(Environments.Production);
        var error = Assert.Throws<InvalidOperationException>(() =>
            new ServiceCollection().AddHonuaLicensing(configuration, environment));
        Assert.Contains("Licensing:Mode must be Enabled or Disabled", error.Message, StringComparison.Ordinal);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            FileBackedLicenseService.LoadBootstrapSnapshotAsync(configuration, NullLoggerFactory.Instance));
    }

    [UnitTest]
    public async Task Disabled_Bootstrap_IgnoresLicenseSourcesAndGrantsPaidStartupEntitlements()
    {
        var configuration = Configuration("Disabled", poisonedSources: true);
        var resolver = Substitute.For<ILicenseContentSecretResolver>();
        var snapshot = await FileBackedLicenseService.LoadBootstrapSnapshotAsync(
            configuration, NullLoggerFactory.Instance, secretResolvers: [resolver]);
        Assert.Equal(LicenseMode.Disabled, snapshot.Mode);
        Assert.Equal(HonuaEdition.Enterprise, snapshot.Edition);
        Assert.Null(snapshot.ExpiresAt);
        Assert.All(FeatureCatalog.All, feature => Assert.True(snapshot.HasEntitlement(feature.Key)));
        Assert.Empty(resolver.ReceivedCalls());
        var environment = Substitute.For<IHostEnvironment>();
        environment.EnvironmentName.Returns(Environments.Production);
        Assert.True(await StartupConfigurationHelpers.IsRedisCacheEntitledAsync(configuration, environment));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Enabled")]
    [Trait("Tier", "Fast")]
    public async Task EnabledOrOmitted_WithoutSource_RemainsCommunity(string? mode)
    {
        var configuration = Configuration(mode);
        var snapshot = await FileBackedLicenseService.LoadBootstrapSnapshotAsync(configuration, NullLoggerFactory.Instance);
        Assert.Equal(HonuaEdition.Community, snapshot.Edition);
        Assert.Equal(LicenseMode.Enabled, snapshot.Mode);
        Assert.False(snapshot.HasEntitlement("caching.redis"));
        var environment = Substitute.For<IHostEnvironment>();
        environment.EnvironmentName.Returns(Environments.Production);
        var services = new ServiceCollection().AddLogging().AddHonuaLicensing(configuration, environment);
        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(FileBackedLicenseService));
        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(LicenseCapacityMeter));
    }

    [Theory]
    [InlineData("Enabled")]
    [InlineData("Disabled")]
    [Trait("Tier", "Fast")]
    public void Production_DevGrant_RemainsForbidden(string mode)
    {
        var configuration = Configuration(mode);
        configuration["Licensing:DevGrantEdition"] = "Enterprise";
        var environment = Substitute.For<IHostEnvironment>();
        environment.EnvironmentName.Returns(Environments.Production);
        var error = Assert.Throws<InvalidOperationException>(() =>
            new ServiceCollection().AddHonuaLicensing(configuration, environment));
        Assert.Contains("DevGrantEdition is a test/dev-only", error.Message, StringComparison.Ordinal);
    }

    [UnitTest]
    public async Task Disabled_ProductionHost_NoLicenseTimersOrMeterDependenciesAndAllGatesActive()
    {
        var configuration = Configuration("Disabled", poisonedSources: true);
        using var host = new HostBuilder()
            .UseEnvironment(Environments.Production)
            .ConfigureServices((context, services) =>
            {
                services.AddSingleton<TimeProvider>(new RejectTimerTimeProvider());
                services.AddSingleton<IConnectionMultiplexer>(_ => throw new InvalidOperationException("License meter must not resolve Redis."));
                services.AddHonuaLicensing(configuration, context.HostingEnvironment);
                Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(FileBackedLicenseService));
                Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(LicenseCapacityMeter));
                Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(IEd25519Verifier));
                Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(ILicenseContentSecretResolver));
            }).Build();
        await host.StartAsync();
        try
        {
            var service = host.Services.GetRequiredService<ILicenseEntitlementService>();
            Assert.All(FeatureCatalog.All, feature =>
            {
                Assert.True(service.CheckEntitlement(feature.Key).IsActive);
                Assert.True(LicenseGate.CheckEntitlement(host.Services, feature.Key).IsActive);
            });
            Assert.False(service.CheckEntitlement("unknown.synthetic.entitlement").IsActive);
            var policy = host.Services.GetRequiredService<ILicenseOperationPolicy>();
            Assert.False(policy.IsBlocked);
            Assert.False(policy.OperationCancellation.CanBeCanceled);
            var provider = host.Services.GetRequiredService<ILicenseStatusProvider>();
            Assert.Equal(LicenseMode.Disabled, provider.GetCurrentStatus().Mode);
            var manager = host.Services.GetRequiredService<ILicenseManager>();
            Assert.Equal("Unlicensed-2026.1", (await manager.GetLicenseInfoAsync()).Edition);
            Assert.All(await manager.GetEntitlementsAsync(), entitlement => Assert.True(entitlement.IsActive));
            using var upload = new MemoryStream([1, 2, 3]);
            Assert.False((await provider.UploadLicenseAsync(upload)).Success);
            Assert.Equal(0, upload.Position);
            await Assert.ThrowsAsync<LicenseUploadRejectedException>(() => manager.ApplyLicenseAsync([1, 2, 3]));

            var meter = host.Services.GetRequiredService<ILicenseCapacityMeter>();
            for (var index = 0; index < 3; index++)
            {
                var decision = await meter.RegisterInstanceAsync(new LicenseCapacityRegistrationRequest
                {
                    InstanceId = $"synthetic-instance-{index}",
                    ServingUnits = 1000,
                    DeploymentRole = LicenseDeploymentRole.Production,
                    Topology = LicenseServingTopology.ReplicaSet
                });
                Assert.True(decision.IsAccepted);
                AssertDisabledCapacity(decision.State);
            }
            AssertDisabledCapacity(await meter.SetSurgeModeAsync(true, "synthetic surge"));
            AssertDisabledCapacity(await meter.GetCapacityStateAsync());
        }
        finally
        {
            await host.StopAsync();
        }
    }

    private static void AssertDisabledCapacity(LicenseCapacityState state)
    {
        Assert.False(state.MeteringEnabled);
        Assert.Equal(LicenseCapacityBandState.Disabled, state.State);
        Assert.False(state.RegistrationEnforced);
        Assert.False(state.RedisCoordinated);
        Assert.False(state.MeteringGap);
        Assert.Equal(0, state.LiveInstanceCount);
        Assert.Equal(0, state.CurrentServingUnits);
        Assert.Equal(0, state.P95ServingUnits);
        Assert.Null(state.Terms);
        Assert.Null(state.GraceExpiresAt);
        Assert.False(state.Surge.IsActive);
        Assert.False(state.Warning80Percent);
        Assert.False(state.Warning100Percent);
    }

    private static IConfigurationRoot Configuration(string? mode, bool poisonedSources = false)
        => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Licensing:Mode"] = mode,
            ["Licensing:Edition"] = poisonedSources ? "Enterprise" : null,
            ["Licensing:LicensePath"] = poisonedSources ? "/nonexistent/disabled-license.json" : null,
            ["Licensing:LicenseContent"] = poisonedSources ? "not a signed license" : null,
            ["Licensing:LicenseContentSecretRef"] = poisonedSources ? "aws:secretsmanager:synthetic-unreachable-license" : null,
            ["Licensing:TrustedKeys:synthetic"] = "not a signing key",
            ["Licensing:AllowAdminUpload"] = "true",
            ["Licensing:Capacity:RegistrationEnabled"] = "true",
            ["ConnectionStrings:redis"] = "localhost:6379"
        }).Build();

    private sealed class RejectTimerTimeProvider : TimeProvider
    {
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
            => throw new InvalidOperationException("Disabled licensing must not schedule expiry or metering timers.");
    }
}

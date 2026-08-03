// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Licensing.Abstractions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Infrastructure.Monitoring;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Helpers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Licensing;

/// <summary>
/// Guards the output-cache entitlement gate (<c>caching.output-cache</c>, Pro; #2998).
/// <c>Program</c> wires <c>UseOutputCache()</c> inside an <c>app.UseWhen(...)</c> branch whose
/// predicate checks the live license snapshot instead of capturing it once at process boot. A
/// Redis-backed output cache additionally requires the live <c>caching.redis</c> entitlement. That
/// matters in both directions: a Community process upgraded through <see cref="ILicenseManager"/>
/// must start caching without a restart, and a paid license that expires or loses the Redis
/// entitlement at runtime must stop serving cached responses immediately.
/// </summary>
[Protocol(TestProtocols.Admin)]
[Operation(Operations.LicenseManagement)]
public sealed class OutputCacheEntitlementGateTests
{
    [UnitTest]
    public void OutputCacheGate_ProEdition_EntitlesOutputCache()
    {
        var context = BuildContext(new MutableLicenseEntitlementService(HonuaEdition.Pro));

        IsOutputCacheEntitled(context).Should().BeTrue(
            "caching.output-cache is a Pro entitlement, so a Pro snapshot must enable the branch");
    }

    [UnitTest]
    public void OutputCacheGate_CommunityEdition_DoesNotEntitleOutputCache()
    {
        var context = BuildContext(new MutableLicenseEntitlementService(HonuaEdition.Community));

        IsOutputCacheEntitled(context).Should().BeFalse(
            "without a paid license the Community snapshot does not include caching.output-cache");
    }

    [UnitTest]
    public void OutputCacheGate_NoEntitlementServiceRegistered_DoesNotEntitleOutputCache()
    {
        // Fail closed: a host that never registered the licensing services must not cache.
        var context = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().BuildServiceProvider()
        };

        IsOutputCacheEntitled(context).Should().BeFalse();
    }

    [UnitTest]
    public void OutputCacheGate_AfterRuntimeUpgradeToPro_EntitlesOutputCache()
    {
        // A Community process that has a Pro license applied at runtime (ILicenseManager) must
        // begin caching on the next request; a boot-time capture would stay off until restart.
        var entitlements = new MutableLicenseEntitlementService(HonuaEdition.Community);
        var context = BuildContext(entitlements);

        IsOutputCacheEntitled(context).Should().BeFalse();

        entitlements.Apply(HonuaEdition.Pro);

        IsOutputCacheEntitled(context).Should().BeTrue(
            "the gate must re-read the license snapshot per request, not freeze the boot-time answer");
    }

    [UnitTest]
    public void OutputCacheGate_AfterRuntimeExpiry_StopsEntitlingOutputCache()
    {
        // The serious direction: an expired Pro license must not keep the middleware wired and
        // keep serving cached responses it is no longer entitled to.
        var entitlements = new MutableLicenseEntitlementService(HonuaEdition.Pro);
        var context = BuildContext(entitlements);

        IsOutputCacheEntitled(context).Should().BeTrue();

        entitlements.Expire();

        IsOutputCacheEntitled(context).Should().BeFalse(
            "once the license snapshot reports Expired the output-cache branch must stop running");
    }

    [UnitTest]
    public void OutputCacheGate_RedisBackendWithoutLiveRedisEntitlement_DoesNotEntitleOutputCache()
    {
        var entitlements = new MutableLicenseEntitlementService(HonuaEdition.Pro);
        var context = BuildContext(entitlements);

        IsOutputCacheEntitled(context, redisOutputCacheConfigured: true).Should().BeTrue();

        entitlements.Apply(HonuaEdition.Pro, [FeatureCatalog.OutputCacheKey]);

        IsOutputCacheEntitled(context, redisOutputCacheConfigured: true).Should().BeFalse(
            "a Redis-backed output cache must stop after a hot license replacement removes caching.redis");
    }

    [UnitTest]
    public void OutputCacheGate_LocalBackendWithoutRedisEntitlement_StillEntitlesOutputCache()
    {
        var entitlements = new MutableLicenseEntitlementService(HonuaEdition.Pro);
        var context = BuildContext(entitlements);

        entitlements.Apply(HonuaEdition.Pro, [FeatureCatalog.OutputCacheKey]);

        IsOutputCacheEntitled(context, redisOutputCacheConfigured: false).Should().BeTrue(
            "the local output-cache store does not require the separate Redis entitlement");
    }

    [UnitTest]
    public void OutputCacheStore_RedisConfiguredWithoutRedisEntitlement_UsesLocalStore()
    {
        var store = ResolveOutputCacheStore(redisCacheEntitled: false);

        store.GetType().Name.Should().NotContain(
            "Redis",
            "a Redis connection string must not bypass the caching.redis entitlement");
    }

    [UnitTest]
    public void OutputCacheStore_RedisConfiguredWithRedisEntitlement_UsesRedisStore()
    {
        var store = ResolveOutputCacheStore(redisCacheEntitled: true);

        store.GetType().Name.Should().Contain(
            "Redis",
            "the entitled Redis backend should replace only the local output-cache store");
    }

    [UnitTest]
    public void OutputCacheKey_SameEditionWithDifferentEntitlements_UsesDifferentFingerprint()
    {
        var first = BuildStatusContext(new LicenseStatus(
            HonuaEdition.Pro,
            IsValid: true,
            ExpiresAt: null,
            LicensedTo: "test",
            ValidationState: LicenseValidationState.Valid,
            Entitlements:
            [
                new Entitlement { Key = FeatureCatalog.OutputCacheKey, Name = "Output cache", IsActive = true },
                new Entitlement { Key = "metadata.extended", Name = "Extended metadata", IsActive = true },
            ]));
        var second = BuildStatusContext(new LicenseStatus(
            HonuaEdition.Pro,
            IsValid: true,
            ExpiresAt: null,
            LicensedTo: "test",
            ValidationState: LicenseValidationState.Valid,
            Entitlements:
            [
                new Entitlement { Key = FeatureCatalog.OutputCacheKey, Name = "Output cache", IsActive = true },
            ]));

        var firstKey = ObservabilityServiceCollectionExtensions.ResolveLicenseOutputCacheKey(first);
        var secondKey = ObservabilityServiceCollectionExtensions.ResolveLicenseOutputCacheKey(second);

        firstKey.Key.Should().Be("license");
        firstKey.Value.Should().NotBe(
            secondKey.Value,
            "same-edition licenses with different metadata entitlements cannot share cached responses");
    }

    [UnitTest]
    public void Program_WiresOutputCache_BehindTheLiveEntitlementPredicate()
    {
        // The runtime-change behaviour above is only real if Program actually consults the live
        // snapshot per request. Guard the wiring itself so a return to the boot-time capture
        // (`if (outputCacheEntitled) { app.UseOutputCache(); }`) fails here rather than silently
        // reintroducing an entitlement that outlives the license.
        var source = File.ReadAllText(ResolveProgramPath());

        var useWhenIndex = source.IndexOf("app.UseWhen(", StringComparison.Ordinal);
        useWhenIndex.Should().BeGreaterThan(-1, "output caching must be a conditional pipeline branch");

        var branch = source[useWhenIndex..];
        var outputCacheIndex = branch.IndexOf("UseOutputCache()", StringComparison.Ordinal);
        outputCacheIndex.Should().BeGreaterThan(-1, "UseOutputCache() must sit inside the branch builder");

        var guarded = branch[..outputCacheIndex];
        guarded.Should().Contain(
            nameof(ObservabilityServiceCollectionExtensions.HasLiveOutputCacheEntitlements),
            "the branch predicate must read the live license snapshot");
        guarded.Should().Contain(
            "redisOutputCacheConfigured",
            "the branch predicate must require the live Redis entitlement for a Redis backend");

        source.Should().NotContain(
            "IsOutputCacheEntitledAsync",
            "the boot-time capture is what froze the entitlement for the process lifetime");
        source.Should().Contain(
            "AddServerFeatures(builder.Configuration, redisCacheEntitled)",
            "the Redis output-cache backend must receive the boot-time caching.redis decision");
    }

    private static bool IsOutputCacheEntitled(
        HttpContext context,
        bool redisOutputCacheConfigured = false)
        => ObservabilityServiceCollectionExtensions.HasLiveOutputCacheEntitlements(
            context.RequestServices,
            redisOutputCacheConfigured);

    private static IOutputCacheStore ResolveOutputCacheStore(bool redisCacheEntitled)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:redis"] = "localhost:6379",
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        ObservabilityServiceCollectionExtensions.ConfigureOutputCaching(
            services,
            configuration,
            redisCacheEntitled);

        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOutputCacheStore>();
    }

    private static string ResolveProgramPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            // False positive: all later segments are fixed relative literals, never absolute.
            var candidate = Path.Join(directory.FullName, "src", "Honua.Server", "Program.cs");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            "Could not locate src/Honua.Server/Program.cs from the test base directory.");
    }

    private static DefaultHttpContext BuildContext(ILicenseEntitlementService entitlements)
    {
        var services = new ServiceCollection();
        services.AddSingleton(entitlements);
        return new DefaultHttpContext
        {
            RequestServices = services.BuildServiceProvider()
        };
    }

    private static DefaultHttpContext BuildStatusContext(LicenseStatus status)
    {
        var provider = new StaticLicenseStatusProvider(status);
        var services = new ServiceCollection();
        services.AddSingleton<ILicenseStatusProvider>(provider);
        return new DefaultHttpContext
        {
            RequestServices = services.BuildServiceProvider()
        };
    }

    private sealed class StaticLicenseStatusProvider(LicenseStatus status) : ILicenseStatusProvider
    {
        public LicenseStatus GetCurrentStatus() => status;

        public Task<LicenseUploadResult> UploadLicenseAsync(
            Stream licenseStream,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new LicenseUploadResult(false, "Static test provider does not support uploads."));
    }

    /// <summary>
    /// Stands in for the runtime <c>FileBackedLicenseService</c>: a singleton whose snapshot is
    /// republished when a license is applied or expires, exactly as
    /// <c>ApplyLicenseAsync</c> and <c>GetSnapshot</c>'s lazy expiration transition do.
    /// </summary>
    private sealed class MutableLicenseEntitlementService : ILicenseEntitlementService
    {
        private LicenseSnapshot _snapshot;

        public MutableLicenseEntitlementService(HonuaEdition edition)
            => _snapshot = LicenseTestSupport.CreateSnapshot(edition);

        public void Apply(HonuaEdition edition, string[]? entitlements = null)
            => _snapshot = LicenseTestSupport.CreateSnapshot(edition, entitlements: entitlements);

        public void Expire()
            => _snapshot = LicenseTestSupport.CreateSnapshot(
                HonuaEdition.Community,
                LicenseValidationState.Expired,
                entitlements: []);

        public LicenseSnapshot GetSnapshot() => _snapshot;

        public LicenseEntitlementDecision CheckEntitlement(string entitlementKey)
        {
            var active = _snapshot.HasEntitlement(entitlementKey);
            return new LicenseEntitlementDecision(
                entitlementKey,
                active,
                _snapshot.Edition,
                _snapshot.ValidationState,
                RequiredEdition: null,
                UpgradeMessage: active ? string.Empty : $"'{entitlementKey}' is not active.");
        }
    }
}

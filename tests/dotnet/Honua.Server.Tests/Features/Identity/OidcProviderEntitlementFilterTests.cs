// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Identity.Abstractions;
using Honua.Core.Features.Identity.Domain;
using Honua.Core.Features.Licensing.Abstractions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Server.Features.Admin;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Helpers;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Identity;

/// <summary>
/// Unit-level tests for the OIDC provider admin entitlement filter
/// (<c>OidcProviderEndpoints.ApplyProviderEntitlementGatesAsync</c>, #2997). These exercise the
/// two invariants that the HTTP-level tests in
/// <see cref="OidcEntitlementGateTests"/> cannot pin down deterministically:
/// <list type="bullet">
/// <item>the create route is identified from endpoint metadata, so the connectivity-test route is
/// never mistaken for a create (a request-path suffix check misclassified the equally valid
/// trailing-slash form <c>…/providers/{id}/test/</c>);</item>
/// <item>the provider-count check and the store mutation are one critical section, so concurrent
/// creates cannot all observe an empty store and collectively bypass the Enterprise
/// <c>identity.oidc-multi-provider</c> entitlement.</item>
/// </list>
/// </summary>
[Protocol(TestProtocols.Admin)]
[Operation(Operations.IdentityManagement)]
public sealed class OidcProviderEntitlementFilterTests
{
    [UnitTest]
    public async Task ProviderEntitlementFilter_TestRouteWithTrailingSlash_ReachesTheHandler()
    {
        // Pro grants identity.oidc but not identity.oidc-multi-provider, and a provider already
        // exists — so if the filter misclassifies this request as a create, it answers 402.
        var store = new FakeOidcProviderStore(existingProviders: 1);
        var context = BuildContext(
            store,
            HonuaEdition.Pro,
            path: $"/api/v1/admin/oidc/providers/{Guid.NewGuid()}/test/",
            isCreateRoute: false);

        var handlerRan = false;
        var result = await OidcProviderEndpoints.ApplyProviderEntitlementGatesAsync(
            EndpointFilterInvocationContext.Create(context),
            _ =>
            {
                handlerRan = true;
                return ValueTask.FromResult<object?>("tested");
            });

        Assert.True(
            handlerRan,
            "the connectivity-test route creates nothing, so the multi-provider gate must not fire " +
            "for it — including for the trailing-slash form routing still matches");
        Assert.Equal("tested", result);
    }

    [UnitTest]
    public async Task ProviderEntitlementFilter_ConcurrentCreatesAtPro_AdmitOnlyOneProvider()
    {
        // Pro may configure exactly one provider. Without the count check and the store mutation
        // sharing a critical section, every racing request observes an empty store, passes the
        // preflight, and is accepted — bypassing the Enterprise multi-provider entitlement.
        //
        // The store holds each caller inside ListProvidersAsync until both have arrived, which
        // makes the check-then-act window deterministic rather than timing-dependent: a
        // serialized implementation can only ever have one caller inside, so it falls through on
        // the barrier's own timeout and still creates exactly one provider.
        const int Racers = 2;
        var store = new FakeOidcProviderStore(
            existingProviders: 0,
            concurrentArrivals: Racers,
            arrivalTimeout: TimeSpan.FromMilliseconds(750));

        var attempts = Enumerable.Range(0, Racers).Select(index =>
        {
            var context = BuildContext(
                store,
                HonuaEdition.Pro,
                path: "/api/v1/admin/oidc/providers",
                isCreateRoute: true);

            return Task.Run(async () => await OidcProviderEndpoints.ApplyProviderEntitlementGatesAsync(
                EndpointFilterInvocationContext.Create(context),
                async _ =>
                {
                    await store.CreateProviderAsync(new OidcProviderConfiguration
                    {
                        Name = $"racer-{index}",
                        ProviderType = "Generic",
                        Authority = "https://idp.example.com",
                        ClientId = $"client-{index}",
                    });

                    return "created";
                }));
        }).ToArray();

        var results = await Task.WhenAll(attempts);

        Assert.Equal(1, results.Count(result => result as string == "created"));
        Assert.Equal(1, store.CreateCount);
        Assert.Single(await store.ListProvidersAsync());
    }

    private static DefaultHttpContext BuildContext(
        IOidcProviderStore store,
        HonuaEdition edition,
        string path,
        bool isCreateRoute)
    {
        var services = new ServiceCollection();
        services.AddSingleton(store);
        services.AddSingleton<ILicenseEntitlementService>(new TestLicenseEntitlementService(edition));

        var metadata = isCreateRoute
            ? new EndpointMetadataCollection(OidcProviderEndpoints.CreateProviderRoute.Instance)
            : EndpointMetadataCollection.Empty;

        var context = new DefaultHttpContext
        {
            RequestServices = services.BuildServiceProvider()
        };
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = path;
        context.SetEndpoint(new Endpoint(_ => Task.CompletedTask, metadata, "oidc-provider-route"));

        return context;
    }

    /// <summary>
    /// Minimal in-memory provider store. When <c>concurrentArrivals</c> is set,
    /// <see cref="ListProvidersAsync"/> holds each caller until that many callers have arrived (or
    /// the arrival timeout elapses), which pins the filter's check-then-act window open
    /// deterministically instead of relying on scheduler timing. The timeout is what lets a
    /// correctly serialized implementation still make progress: it can only ever have one caller
    /// inside, so the barrier must not be a hard rendezvous.
    /// </summary>
    private sealed class FakeOidcProviderStore : IOidcProviderStore
    {
        private readonly List<OidcProviderConfiguration> _providers = [];
        private readonly Lock _sync = new();
        private readonly TaskCompletionSource _allArrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly int _concurrentArrivals;
        private readonly TimeSpan _arrivalTimeout;
        private int _arrivals;
        private int _createCount;

        public FakeOidcProviderStore(
            int existingProviders,
            int concurrentArrivals = 0,
            TimeSpan arrivalTimeout = default)
        {
            _concurrentArrivals = concurrentArrivals;
            _arrivalTimeout = arrivalTimeout;
            for (var index = 0; index < existingProviders; index++)
            {
                _providers.Add(new OidcProviderConfiguration
                {
                    Name = $"existing-{index}",
                    ProviderType = "Generic",
                    Authority = "https://idp.example.com",
                    ClientId = $"client-existing-{index}",
                });
            }
        }

        public int CreateCount => Volatile.Read(ref _createCount);

        public async Task<IReadOnlyList<OidcProviderConfiguration>> ListProvidersAsync(
            CancellationToken cancellationToken = default)
        {
            if (_concurrentArrivals > 0)
            {
                if (Interlocked.Increment(ref _arrivals) >= _concurrentArrivals)
                {
                    _allArrived.TrySetResult();
                }

                await Task.WhenAny(_allArrived.Task, Task.Delay(_arrivalTimeout, cancellationToken));
            }

            lock (_sync)
            {
                return _providers.ToArray();
            }
        }

        public Task<OidcProviderConfiguration?> GetProviderAsync(
            Guid providerId,
            CancellationToken cancellationToken = default)
        {
            lock (_sync)
            {
                return Task.FromResult(_providers.FirstOrDefault(p => p.ProviderId == providerId));
            }
        }

        public Task<OidcProviderConfiguration> CreateProviderAsync(
            OidcProviderConfiguration provider,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _createCount);
            lock (_sync)
            {
                _providers.Add(provider);
            }

            return Task.FromResult(provider);
        }

        public Task<OidcProviderConfiguration?> UpdateProviderAsync(
            OidcProviderConfiguration provider,
            CancellationToken cancellationToken = default)
            => Task.FromResult<OidcProviderConfiguration?>(provider);

        public Task<bool> DeleteProviderAsync(Guid providerId, CancellationToken cancellationToken = default)
            => Task.FromResult(false);

        public Task<OidcProviderTestResult> TestProviderAsync(
            Guid providerId,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new OidcProviderTestResult { IsReachable = true, Message = "ok" });
    }
}

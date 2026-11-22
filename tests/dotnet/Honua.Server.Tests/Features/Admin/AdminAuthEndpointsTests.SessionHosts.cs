// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.TestKit;

namespace Honua.Server.Tests.Features.Admin;

public sealed partial class AdminAuthEndpointsTests
{
    // xUnit runs the cases of one class in sequence, so each partial file holds its cases in a
    // nested class and the groups run in parallel. Within a class, one application per configuration
    // serves every case that needs it. Each case uses fresh clients (own cookie jar) from addresses of
    // their own, so one case's sign-in limit, failed attempts and session never reach another.
    // Sessions persist on the host; cases with their own configuration still own their application.
    public abstract class SessionHost(WebAppFixture fixture) : IAsyncLifetime
    {
        internal WebAppFixture Fixture { get; } = fixture.ConfigureServices(PerClientAddress.Register);

        public Task InitializeAsync() => Fixture.InitializeAsync();

        public virtual Task DisposeAsync() => Fixture.DisposeAsync();

        internal HttpClient CreateBrowser() => PerClientAddress.Assign(Fixture.CreateClient());

        internal HttpClient CreateBrowserWithoutRedirects() =>
            PerClientAddress.Assign(Fixture.CreateClient(allowAutoRedirect: false));
    }

    public sealed class BaseSessionHost() : SessionHost(CreateBaseFixture())
    {
        private readonly SemaphoreSlim _sharedLock = new(1, 1);
        private readonly Dictionary<string, SharedHostEntry> _sharedHosts = new(StringComparer.Ordinal);

        internal Task<SharedHostEntry> GetAzureAdHostAsync()
            => GetOrCreateSharedHostAsync("azuread", static () =>
                new SharedHostEntry(CreateAzureAdFixture().ConfigureServices(PerClientAddress.Register), null));

        internal Task<SharedHostEntry> GetGenericOidcHostAsync()
            => GetOrCreateSharedHostAsync("generic-oidc", static () =>
            {
                var stub = CreateStubFactory();
                return new SharedHostEntry(CreateGenericOidcFixture(stub).ConfigureServices(PerClientAddress.Register), stub);
            });

        internal Task<SharedHostEntry> GetProductionGenericOidcHostAsync()
            => GetOrCreateSharedHostAsync("production-generic-oidc", static () =>
            {
                var stub = CreateStubFactory();
                return new SharedHostEntry(CreateProductionGenericOidcFixture(stub), stub);
            });

        internal Task<SharedHostEntry> GetPublicBaseUrlGenericOidcHostAsync(string? callback)
            => GetOrCreateSharedHostAsync(
                callback is null ? "public-base-url" : $"public-base-url:{callback}",
                () =>
                {
                    var stub = CreateStubFactory();
                    var fixture = CreateGenericOidcFixture(stub)
                        .ConfigureServices(PerClientAddress.Register)
                        .ConfigureWebHost(builder =>
                        {
                            builder.UseSetting("Public:BaseUrl", "https://public.example.com/honua");
                            if (callback is not null)
                            {
                                builder.UseSetting("Oidc:AdminAuthCallbackUri", callback);
                            }
                        });
                    return new SharedHostEntry(fixture, stub);
                });

        internal Task<SharedHostEntry> GetOperatorBearerHostAsync()
            => GetOrCreateSharedHostAsync("operator-bearer", static () =>
            {
                var stub = CreateStubFactory();
                return new SharedHostEntry(CreateOperatorBearerFixture(stub).ConfigureServices(PerClientAddress.Register), stub);
            });

        public override async Task DisposeAsync()
        {
            foreach (var entry in _sharedHosts.Values)
            {
                await entry.DisposeAsync();
            }

            _sharedHosts.Clear();
            _sharedLock.Dispose();
            await base.DisposeAsync();
        }

        private async Task<SharedHostEntry> GetOrCreateSharedHostAsync(string key, Func<SharedHostEntry> create)
        {
            await _sharedLock.WaitAsync();
            try
            {
                if (_sharedHosts.TryGetValue(key, out var existing))
                {
                    existing.Stub?.Reset();
                    return existing;
                }

                var created = create();
                try
                {
                    await created.Fixture.InitializeAsync();
                }
                catch
                {
                    await created.DisposeAsync();
                    throw;
                }

                _sharedHosts[key] = created;
                return created;
            }
            finally
            {
                _sharedLock.Release();
            }
        }
    }

    internal sealed class SharedHostEntry(WebAppFixture fixture, StubHttpClientFactory? stub) : IAsyncDisposable
    {
        public WebAppFixture Fixture { get; } = fixture;

        public StubHttpClientFactory? Stub { get; } = stub;

        public HttpClient CreateBrowser() => PerClientAddress.Assign(Fixture.CreateClient());

        public HttpClient CreateBrowser(Action<HttpClient> configure) =>
            PerClientAddress.Assign(Fixture.CreateClient(configure));

        public HttpClient CreateClient() => Fixture.CreateClient();

        public HttpClient CreateClient(Action<HttpClient> configure) => Fixture.CreateClient(configure);

        public async ValueTask DisposeAsync()
        {
            await Fixture.DisposeAsync();
            Stub?.Dispose();
        }
    }

    public sealed class PasswordTokenSessionHost() : SessionHost(CreatePasswordTokenFixture());

    public sealed class OidcCallbackSessionHost : SessionHost
    {
        private readonly StubHttpClientFactory _identityProvider;

        public OidcCallbackSessionHost()
            : this(CreateCallbackStub())
        {
        }

        private OidcCallbackSessionHost(StubHttpClientFactory identityProvider)
            : base(CreateGenericOidcFixture(identityProvider)) => _identityProvider = identityProvider;

        public override async Task DisposeAsync()
        {
            await base.DisposeAsync();
            _identityProvider.Dispose();
        }
    }
}

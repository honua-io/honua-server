// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.TestKit;
using Microsoft.AspNetCore.Hosting;

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
        private WebAppFixture? _azureAd;
        private (WebAppFixture Fixture, StubHttpClientFactory Stub)? _genericOidc;
        private (WebAppFixture Fixture, StubHttpClientFactory Stub)? _productionGenericOidc;
        private (WebAppFixture Fixture, StubHttpClientFactory Stub)? _operatorBearer;
        private readonly Dictionary<string, (WebAppFixture Fixture, StubHttpClientFactory Stub)> _publicBaseOidc = new(StringComparer.Ordinal);

        internal async Task<WebAppFixture> GetAzureAdHostAsync()
        {
            if (_azureAd is not null)
            {
                return _azureAd;
            }

            var fixture = CreateAzureAdFixture();
            try
            {
                await fixture.InitializeAsync();
                _azureAd = fixture;
                return fixture;
            }
            catch
            {
                await fixture.DisposeAsync();
                throw;
            }
        }

        internal async Task<(WebAppFixture Fixture, StubHttpClientFactory Stub)> GetGenericOidcHostAsync()
        {
            if (_genericOidc is { } existing)
            {
                existing.Stub.Reset();
                return existing;
            }

            var created = await StartStubbedHostAsync(CreateGenericOidcFixture);
            _genericOidc = created;
            return created;
        }

        internal async Task<(WebAppFixture Fixture, StubHttpClientFactory Stub)> GetProductionGenericOidcHostAsync()
        {
            if (_productionGenericOidc is { } existing)
            {
                existing.Stub.Reset();
                return existing;
            }

            var created = await StartStubbedHostAsync(CreateProductionGenericOidcFixture);
            _productionGenericOidc = created;
            return created;
        }

        internal async Task<(WebAppFixture Fixture, StubHttpClientFactory Stub)> GetPublicBaseOidcHostAsync(string? callback)
        {
            var key = callback ?? string.Empty;
            if (_publicBaseOidc.TryGetValue(key, out var existing))
            {
                existing.Stub.Reset();
                return existing;
            }

            var created = await StartStubbedHostAsync(stub =>
                CreateGenericOidcFixture(stub)
                    .ConfigureWebHost(builder =>
                    {
                        builder.UseSetting("Public:BaseUrl", "https://public.example.com/honua");
                        // An unset callback must remain absent: host settings serialize null as empty.
                        if (callback is not null)
                        {
                            builder.UseSetting("Oidc:AdminAuthCallbackUri", callback);
                        }
                    }));
            _publicBaseOidc[key] = created;
            return created;
        }

        internal async Task<(WebAppFixture Fixture, StubHttpClientFactory Stub)> GetOperatorBearerHostAsync()
        {
            if (_operatorBearer is { } existing)
            {
                existing.Stub.Reset();
                return existing;
            }

            var created = await StartStubbedHostAsync(CreateOperatorBearerFixture);
            _operatorBearer = created;
            return created;
        }

        public override async Task DisposeAsync()
        {
            if (_azureAd is not null)
            {
                await _azureAd.DisposeAsync();
            }

            await DisposeStubbedHostAsync(_genericOidc);
            await DisposeStubbedHostAsync(_productionGenericOidc);
            await DisposeStubbedHostAsync(_operatorBearer);
            foreach (var entry in _publicBaseOidc.Values)
            {
                await DisposeStubbedHostAsync(entry);
            }

            await base.DisposeAsync();
        }

        private static async Task<(WebAppFixture Fixture, StubHttpClientFactory Stub)> StartStubbedHostAsync(
            Func<StubHttpClientFactory, WebAppFixture> createFixture)
        {
            var stub = CreateStubFactory();
            var fixture = createFixture(stub);
            try
            {
                await fixture.InitializeAsync();
                return (fixture, stub);
            }
            catch
            {
                await fixture.DisposeAsync();
                stub.Dispose();
                throw;
            }
        }

        private static async Task DisposeStubbedHostAsync((WebAppFixture Fixture, StubHttpClientFactory Stub)? entry)
        {
            if (entry is not { } value)
            {
                return;
            }

            try
            {
                await value.Fixture.DisposeAsync();
            }
            finally
            {
                value.Stub.Dispose();
            }
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

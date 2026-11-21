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

    public sealed class BaseSessionHost() : SessionHost(CreateBaseFixture());

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

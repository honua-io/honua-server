// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.TestKit;

namespace Honua.Server.Tests.Features.Admin;

// xUnit runs the cases of one class in sequence, so each partial file holds its cases in a
// nested class and the groups run in parallel. Every case still owns its application.
public sealed partial class AdminAuthEndpointsTests
{
    // Configured OIDC and certificate cases own their app; boot the default app only when used.
    private sealed class LazyBaseHost
    {
        private HttpClient? _client;

        public WebAppFixture Fixture { get; } = CreateBaseFixture();

        public async Task<HttpClient> GetClientAsync()
        {
            if (_client is null)
            {
                await Fixture.InitializeAsync();
                _client = Fixture.CreateClient();
            }

            return _client;
        }

        public Task DisposeAsync() => Fixture.DisposeAsync();
    }
}

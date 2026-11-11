// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.TestKit;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.Catalog;

public sealed partial class GeoservicesCatalogEndpointTests
{
    public sealed class SoapRasterFixture : IAsyncLifetime
    {
        private readonly Lazy<Task<WebAppFixture>> _host = new(CreateAsync);

        public Task<WebAppFixture> GetHostAsync() => _host.Value;

        public Task InitializeAsync() => Task.CompletedTask;

        public async Task DisposeAsync()
        {
            if (_host.IsValueCreated && _host.Value.IsCompletedSuccessfully)
            {
                var fixture = await _host.Value;
                await fixture.DisposeAsync();
            }
        }

        private static async Task<WebAppFixture> CreateAsync()
        {
            var rasterStore = CreateSoapRasterStore();
            var fixture = new WebAppFixture()
                .ConfigureServices(services => services.AddSingleton(rasterStore));
            try
            {
                await fixture.InitializeAsync();
                return fixture;
            }
            catch
            {
                await fixture.DisposeAsync();
                throw;
            }
        }
    }
}

// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Security.Domain;
using Honua.TestKit;
using Honua.TestKit.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Sharing;

public sealed partial class SharingRestTokenTests
{
    // One application serves every case that uses the default token configuration. Cases
    // with other settings or a replaced credential verifier still build their own.
    //
    // The in-process WebApplicationFactory transport leaves Connection.RemoteIpAddress
    // unset, which a real Kestrel deployment always populates. IP-bound token issuance
    // (client=ip / client=requestip) needs that address, so a startup filter stamps the
    // current client address onto every request. Each case gets its own address from
    // TEST-NET-3, so failed attempts counted per source by the authentication failure
    // limiter never carry over from one case to the next, as with a host per case.
    public sealed class TokenHost : IAsyncLifetime
    {
        private const int FirstClientHost = 10;
        private readonly ClientAddress _clientAddress = new();
        private int _nextClientHost = FirstClientHost;

        public TokenHost()
        {
            var graph = new TestMetadataV2GraphBuilder()
                .AddService(
                    ProtectedServiceId,
                    "Protected token service",
                    protocols: [ServiceProtocols.FeatureServer],
                    accessPolicy: new AccessPolicy())
                .Build();

            Fixture = new WebAppFixture()
                .ReplaceService<IMetadataV2GraphProvider>(new TestMetadataV2GraphProvider(graph))
                .ConfigureWebHost(builder =>
                {
                    builder.UseEnvironment("Test");
                    builder.UseSetting("HONUA_DEV_AUTH", "false");
                    builder.UseSetting("HONUA_ADMIN_PASSWORD", AdminPassword);
                    // Allow the in-process test transport to issue tokens; production
                    // defaults remain RequireHttps=true.
                    builder.UseSetting("Authentication:PortalToken:RequireHttps", "false");
                    builder.UseSetting("Cors:AllowedOrigins:0", "http://localhost:3000");
                    builder.ConfigureServices(services =>
                        services.AddSingleton<IStartupFilter>(new RemoteIpStartupFilter(_clientAddress)));
                });
        }

        public WebAppFixture Fixture { get; }

        public void UseNextClientAddress()
        {
            if (_nextClientHost > byte.MaxValue - 1)
            {
                throw new InvalidOperationException("The token host ran out of TEST-NET-3 client addresses.");
            }

            _clientAddress.Current = new IPAddress([203, 0, 113, (byte)_nextClientHost++]);
        }

        public Task InitializeAsync() => Fixture.InitializeAsync();

        public Task DisposeAsync() => Fixture.DisposeAsync();
    }

    private sealed class ClientAddress
    {
        public IPAddress Current { get; set; } = IPAddress.Parse("203.0.113.10");
    }

    private sealed class RemoteIpStartupFilter(ClientAddress clientAddress) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
            => app =>
            {
                app.Use(async (context, nextMiddleware) =>
                {
                    context.Connection.RemoteIpAddress = clientAddress.Current;
                    await nextMiddleware(context).ConfigureAwait(false);
                });
                next(app);
            };
    }
}

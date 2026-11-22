// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net.WebSockets;
using System.Security.Claims;
using FluentAssertions;
using Honua.Server.Features.Collaboration.Sessions;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Server.Tests.Features.Collaboration.Sessions;

public sealed partial class CollaborationSessionStreamEndpointTests
{
    // The credential stays valid, but the caller loses the right to edit the map; the open stream must end too.
    [IntegrationTest]
    [Endpoint("GET /api/v1/maps/{mapId}/collaboration/sessions/stream")]
    public async Task Stream_MapPermissionWithdrawnAfterJoin_ClosesWithAuthorizationEnded()
    {
        var authorizer = new SwitchableSavedMapCollaborationAuthorizer();
        using var baseFactory = CreateFactory(allowJoin: false);
        using var factory = baseFactory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<ISavedMapCollaborationAuthorizer>();
            services.AddSingleton<ISavedMapCollaborationAuthorizer>(authorizer);
        }));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var (socket, _) = await ConnectWithPortalTokenAsync(factory, cts.Token);
        using var ws = socket;

        authorizer.Allowed = false;

        var closed = await ReceiveUntilCloseAsync(ws, cts.Token);
        closed.CloseStatus.Should().Be(WebSocketCloseStatus.PolicyViolation);
        closed.CloseStatusDescription.Should().Be("authorization-ended");
    }

    private sealed class SwitchableSavedMapCollaborationAuthorizer : ISavedMapCollaborationAuthorizer
    {
        private volatile bool _allowed = true;

        public bool Allowed
        {
            get => _allowed;
            set => _allowed = value;
        }

        public ValueTask<SavedMapCollaborationAuthorizationResult> AuthorizeJoinAsync(
            string mapId,
            ClaimsPrincipal principal,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(Allowed
                ? SavedMapCollaborationAuthorizationResult.Allow()
                : SavedMapCollaborationAuthorizationResult.Forbid("The map permission was withdrawn."));
    }
}

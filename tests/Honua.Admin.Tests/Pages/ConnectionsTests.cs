// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Admin.Components.Connections;
using Honua.Admin.Models;
using Honua.Admin.Pages;
using Honua.Admin.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;

namespace Honua.Admin.Tests.Pages;

public sealed class ConnectionsTests
{
    [Fact]
    public void ConnectionsPage_RendersConnectionsFromService()
    {
        using var ctx = new TestContext();
        ctx.Services.AddMudServices();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var connections = new List<SecureConnectionSummary>
        {
            new()
            {
                ConnectionId = Guid.NewGuid(),
                Name = "primary-db",
                Host = "db.internal",
                Port = 5432,
                DatabaseName = "honua",
                Username = "admin",
                SslMode = "Require",
                SslRequired = true,
                StorageType = "managed",
                IsActive = true,
                HealthStatus = "Healthy",
                CreatedAt = DateTimeOffset.UtcNow,
                CreatedBy = "tester"
            }
        };

        ctx.Services.AddSingleton<ISecureConnectionsClient>(new FakeConnectionsClient(connections));

        var cut = ctx.Render(builder =>
        {
            builder.OpenComponent<MudPopoverProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<Connections>(1);
            builder.CloseComponent();
        });

        cut.WaitForAssertion(() => Assert.Contains("primary-db", cut.Markup));
        Assert.Contains("db.internal", cut.Markup);
        Assert.Contains("Healthy", cut.Markup);
    }

    [Fact]
    public void ConnectionForm_SubmitRequiresPasswordOnCreate()
    {
        using var ctx = new TestContext();
        ctx.Services.AddMudServices();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var model = new ConnectionFormModel
        {
            Name = "primary",
            Host = "db.internal",
            Port = 5432,
            DatabaseName = "honua",
            Username = "admin",
            SslMode = "Require",
            IsEdit = false,
            UseSecretReference = false
        };

        var submitted = false;

        var cut = ctx.Render(builder =>
        {
            builder.OpenComponent<MudPopoverProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<ConnectionForm>(1);
            builder.AddAttribute(2, "Model", model);
            builder.AddAttribute(3, "OnSubmit", EventCallback.Factory.Create<bool>(this, (bool _) => submitted = true));
            builder.CloseComponent();
        });

        cut.Find("[data-testid=connection-save]").Click();

        Assert.False(submitted);
    }

    private sealed class FakeConnectionsClient : ISecureConnectionsClient
    {
        private readonly IReadOnlyList<SecureConnectionSummary> _connections;

        public FakeConnectionsClient(IReadOnlyList<SecureConnectionSummary> connections)
        {
            _connections = connections;
        }

        public Task<ApiResult<IReadOnlyList<SecureConnectionSummary>>> GetConnectionsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(ApiResult.Ok<IReadOnlyList<SecureConnectionSummary>>(_connections));

        public Task<ApiResult<SecureConnectionDetail>> GetConnectionAsync(Guid connectionId, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<ApiResult<SecureConnectionSummary>> CreateConnectionAsync(CreateSecureConnectionRequest request, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<ApiResult<ConnectionTestResult>> TestDraftConnectionAsync(CreateSecureConnectionRequest request, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<ApiResult<SecureConnectionSummary>> UpdateConnectionAsync(Guid connectionId, UpdateSecureConnectionRequest request, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<ApiResult<bool>> DeleteConnectionAsync(Guid connectionId, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<ApiResult<ConnectionTestResult>> TestConnectionAsync(Guid connectionId, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
    }
}

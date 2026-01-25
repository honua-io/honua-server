// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Admin.Models;
using Honua.Admin.Pages;
using Honua.Admin.Services;
using MudBlazor;
using MudBlazor.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Admin.Tests.Pages;

public sealed class LayersTests
{
    [Fact]
    public void LayersPage_RendersDiscoveredTablesAndPublishedLayers()
    {
        using var ctx = new TestContext();
        ctx.Services.AddMudServices();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var connectionId = Guid.NewGuid();
        var connections = new List<SecureConnectionSummary>
        {
            new()
            {
                ConnectionId = connectionId,
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

        var tableResponse = new TableDiscoveryResponse
        {
            Tables =
            [
                new TableInfo
                {
                    Schema = "public",
                    Table = "parcels",
                    GeometryColumn = "geom",
                    GeometryType = "Polygon",
                    Srid = 4326,
                    EstimatedRows = 120,
                    Columns =
                    [
                        new ColumnInfo
                        {
                            Name = "id",
                            DataType = "integer",
                            IsNullable = false,
                            IsPrimaryKey = true,
                            MaxLength = null
                        },
                        new ColumnInfo
                        {
                            Name = "owner",
                            DataType = "text",
                            IsNullable = true,
                            IsPrimaryKey = false,
                            MaxLength = null
                        }
                    ]
                }
            ]
        };

        var publishedLayers = new List<PublishedLayerSummary>
        {
            new()
            {
                LayerId = 10,
                LayerName = "Parcels",
                Schema = "public",
                Table = "parcels",
                GeometryType = "Polygon",
                Srid = 4326,
                Enabled = true,
                ServiceName = "default"
            }
        };

        ctx.Services.AddSingleton<ISecureConnectionsClient>(new FakeConnectionsClient(connections));
        ctx.Services.AddSingleton<ILayerPublishingClient>(new FakeLayerPublishingClient(tableResponse, publishedLayers));

        var cut = ctx.Render(builder =>
        {
            builder.OpenComponent<MudPopoverProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<Layers>(1);
            builder.CloseComponent();
        });

        cut.WaitForAssertion(() => Assert.Contains("parcels", cut.Markup));
        cut.WaitForAssertion(() => Assert.Contains("Parcels", cut.Markup));
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

    private sealed class FakeLayerPublishingClient : ILayerPublishingClient
    {
        private readonly TableDiscoveryResponse _tableResponse;
        private readonly IReadOnlyList<PublishedLayerSummary> _publishedLayers;

        public FakeLayerPublishingClient(TableDiscoveryResponse tableResponse, IReadOnlyList<PublishedLayerSummary> publishedLayers)
        {
            _tableResponse = tableResponse;
            _publishedLayers = publishedLayers;
        }

        public Task<ApiResult<TableDiscoveryResponse>> GetTablesAsync(Guid connectionId, CancellationToken cancellationToken = default)
            => Task.FromResult(ApiResult.Ok(_tableResponse));

        public Task<ApiResult<IReadOnlyList<PublishedLayerSummary>>> GetPublishedLayersAsync(Guid connectionId, string? serviceName = null, CancellationToken cancellationToken = default)
            => Task.FromResult(ApiResult.Ok<IReadOnlyList<PublishedLayerSummary>>(_publishedLayers));

        public Task<ApiResult<PublishedLayerSummary>> PublishLayerAsync(Guid connectionId, PublishLayerRequest request, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<ApiResult<PublishedLayerSummary>> SetLayerEnabledAsync(Guid connectionId, int layerId, bool enabled, string? serviceName = null, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
    }
}

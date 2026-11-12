// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Ai.Protocols.Mcp;
using Honua.Ai.Protocols.Mcp.MapTools;
using Honua.Ai.Protocols.Mcp.Tools;
using Honua.Core.Configuration;
using Honua.Core.Features.Attachments.Abstractions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Db.Postgres.Features.Attachments;
using Honua.TestKit;
using Honua.TestKit.Helpers;
using Honua.TestKit.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Admin;

public sealed partial class LayerPublishingIntegrationTests
{
    public sealed class PublishingFixture : IAsyncLifetime
    {
        private MetadataV2Graph _initialGraph = null!;
        private long _revision;

        public WebAppFixture Host { get; } = new WebAppFixture().WithTestLicense(HonuaEdition.Pro);
        private int? MissingSridLayerId { get; set; }

        public PublishingFixture()
        {
            Host.DecorateService<IFeatureCacheManager>(inner =>
                new MissingSridCacheManager(inner, layerId => layerId == MissingSridLayerId));
            // Program skips provider infrastructure in Test, and WebAppFixture installs
            // the graph provider after AddServerFeatures has run. Register the same
            // catalog-only MCP surface after that fixture provider composition.
            Host.ConfigureServices(services =>
            {
                services.AddMcpCatalogPublicationSurface();
                // The Test host composes MCP before the fixture graph provider exists, so
                // honua_query_features is absent unless this production tool is registered here.
                services.TryAddEnumerable(ServiceDescriptor.Singleton<IMcpTool, QueryFeaturesTool>());
                services.TryAddEnumerable(ServiceDescriptor.Singleton<IMcpTool, ListLayersTool>());
                services.TryAddEnumerable(ServiceDescriptor.Singleton<IMcpTool, DescribeLayerTool>());
                // Publication metadata is stored in the canonical honua schema. Use its
                // production attachment authority as well; the fixture's isolated feature
                // schema is not a second attachment authority for these publications.
                services.RemoveAll<PostgresAttachmentStore>();
                services.AddScoped(serviceProvider => new PostgresAttachmentStore(
                    serviceProvider.GetRequiredService<IAdoNetDatabaseConnectionProvider>(),
                    serviceProvider.GetService<ICloudFileStorage>(),
                    serviceProvider.GetRequiredService<ILogger<PostgresAttachmentStore>>(),
                    schemaName: "honua",
                    orphanLedger: serviceProvider.GetRequiredService<IAttachmentOrphanLedger>(),
                    attachmentLimits: serviceProvider.GetService<IOptions<LimitsOptions>>()?.Value.Attachments
                        ?? new AttachmentLimits(),
                    migrationContract: Honua.Server.Startup.ServerCoreSchemaMigrations.Manifest.Attachments!));
            });
        }

        public async Task InitializeAsync()
        {
            await Host.InitializeAsync();
            _initialGraph = Host.GetCurrentV2GraphSnapshot().Graph;
            _revision = _initialGraph.Revision;
        }

        public async Task ResetAsync()
        {
            await ClearMissingSridAsync();
            await ServerTestData.SeedAsync(Host.Postgres, Host.CurrentSchema!);
            await ServerTestData.EnsureFixtureAttachmentsAsync(Host.Postgres, "honua");
            await Host.EnsureTestSecureConnectionAsync();
            using var scope = Host.Services.CreateScope();
            var provider = scope.ServiceProvider.GetRequiredService<TestMetadataV2GraphProvider>();
            var current = provider.GetCurrentForSchema(Host.CurrentSchema);
            var baseline = provider.GetCurrentForSchema(string.Empty);
            // ETags contain the revision, so a reused host must never repeat it.
            _revision = Math.Max(_revision, Math.Max(current.Graph.Revision, baseline.Graph.Revision)) + 1;
            SetGraph(_initialGraph with { Revision = _revision });
        }

        public void SetGraph(MetadataV2Graph graph)
        {
            using var scope = Host.Services.CreateScope();
            var provider = scope.ServiceProvider.GetRequiredService<TestMetadataV2GraphProvider>();
            provider.SetGraph(graph, schema: Host.CurrentSchema);
            // Admin publication can initialize either partition. Keep request-schema
            // and baseline views aligned when a test changes the graph directly.
            provider.SetGraph(graph, schema: string.Empty);
        }

        public async Task SimulateMissingCatalogSridAsync(int layerId)
        {
            // Model a missing catalog SRID without altering the shared catalog's NOT NULL
            // constraint. The stored row and transform expression index still require a
            // valid SRID even for an attribute-only UPDATE.
            MissingSridLayerId = layerId;
            await Host.Postgres.ExecuteDdlUnderLockAsync($"""
                CREATE INDEX "idx_srid_{layerId}" ON "{Host.CurrentSchema}".features
                    USING gist (ST_Transform(geometry, 3857)) WHERE layer_id = {layerId};
                """);
        }

        public async Task ClearMissingSridAsync()
        {
            if (MissingSridLayerId is not { } layerId)
            {
                return;
            }

            MissingSridLayerId = null;
            await Host.Postgres.ExecuteDdlUnderLockAsync($"DROP INDEX IF EXISTS \"{Host.CurrentSchema}\".\"idx_srid_{layerId}\";");
        }

        public Task DisposeAsync() => Host.DisposeAsync();
    }
}

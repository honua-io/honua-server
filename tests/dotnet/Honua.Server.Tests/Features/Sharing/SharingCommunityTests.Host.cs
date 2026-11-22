// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Server.Features.Portal.Sharing;
using Honua.TestKit;
using Honua.TestKit.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Honua.Server.Tests.Features.Sharing;

public sealed partial class SharingCommunityTests
{
    // One application with the durable PostgreSQL sharing stores serves every case that uses
    // them; the portal sharing tables are created once. Each case starts from empty group and
    // share tables, as it did with a new schema per case. Cases that need a different binding
    // still build their own application.
    public sealed class CommunityHost : IAsyncLifetime
    {
        public CommunityHost()
        {
            Fixture = new WebAppFixture()
                .ReplaceService<IMetadataV2GraphProvider>(new TestMetadataV2GraphProvider(BuildGraph()))
                .ConfigureWebHost(builder =>
                {
                    builder.UseEnvironment("Test");
                    builder.UseSetting("HONUA_DEV_AUTH", "false");
                    builder.UseSetting("HONUA_ADMIN_PASSWORD", AdminPassword);
                    builder.UseSetting("Authentication:PortalToken:RequireHttps", "false");
                });
            Fixture.ConfigureServices(BindDurableSharingStores);
        }

        public WebAppFixture Fixture { get; }

        public async Task InitializeAsync()
        {
            await Fixture.InitializeAsync();
            var schema = Fixture.CurrentSchema
                ?? throw new InvalidOperationException("Portal fixture schema is missing.");
            await using var stream = typeof(PostgresPortalGroupStore).Assembly.GetManifestResourceStream(
                "Honua.Server.Migrations.133_CreatePortalSharing.sql")
                ?? throw new InvalidOperationException("Portal sharing migration resource is missing.");
            using var reader = new StreamReader(stream);
            using var builder = new NpgsqlCommandBuilder();
            var migration = (await reader.ReadToEndAsync())
                .Replace("$HonuaSchema$", builder.QuoteIdentifier(schema), StringComparison.Ordinal);
            await Fixture.Postgres.RunUnderSchemaMutationLockAsync(async () =>
            {
                await using var connection = await Fixture.Postgres.GetConnectionAsync(schema);
                await using var command = new NpgsqlCommand(migration, connection);
                await command.ExecuteNonQueryAsync();
            });
        }

        public Task DisposeAsync() => Fixture.DisposeAsync();

        public async Task ClearSharingStateAsync()
        {
            await using var connection = await Fixture.Postgres.GetConnectionAsync(Fixture.CurrentSchema!);
            await using var command = new NpgsqlCommand(
                "DELETE FROM portal_item_shares; DELETE FROM portal_community_groups;", connection);
            await command.ExecuteNonQueryAsync();
        }

        private void BindDurableSharingStores(IServiceCollection services)
        {
            // The Test host skips the production composition root. Bind the
            // isolated PostgreSQL profile used by the real Portal sharing path.
            var schema = Fixture.CurrentSchema
                ?? throw new InvalidOperationException("Portal fixture schema is missing.");
            services.RemoveAll<IPortalGroupStore>();
            services.RemoveAll<IPortalItemSharingStore>();
            services.RemoveAll<IPortalAudienceSnapshotProvider>();
            services.AddSingleton<IPortalGroupStore>(sp => new PostgresPortalGroupStore(
                sp.GetRequiredService<NpgsqlDataSource>(), TimeProvider.System, schema));
            services.AddSingleton<IPortalItemSharingStore>(sp => new PostgresPortalItemSharingStore(
                sp.GetRequiredService<NpgsqlDataSource>(), sp.GetRequiredService<IHttpContextAccessor>(), schema));
            services.AddSingleton<IPortalAudienceSnapshotProvider>(sp => new PostgresPortalAudienceSnapshotProvider(
                sp.GetRequiredService<NpgsqlDataSource>(), schema));
        }

        private static MetadataV2Graph BuildGraph()
        {
            var graph = new TestMetadataV2GraphBuilder()
                .AddService("svc-rivers", "Rivers", protocols: [ServiceProtocols.FeatureServer],
                    accessPolicy: new AccessPolicy { AllowedRoles = ["publisher"] })
                .AddService("svc-owned", "Owned", protocols: [ServiceProtocols.FeatureServer],
                    accessPolicy: new AccessPolicy { AllowedRoles = ["publisher"] })
                .AddService("content-a", "First item", protocols: [ServiceProtocols.FeatureServer],
                    accessPolicy: new AccessPolicy { AllowAnonymous = true })
                .AddService("content-b", "Second item", protocols: [ServiceProtocols.MapServer],
                    accessPolicy: new AccessPolicy { AllowAnonymous = true })
                .AddService("content-private", "Restricted item", protocols: [ServiceProtocols.FeatureServer],
                    accessPolicy: new AccessPolicy { AllowedRoles = ["parcel-admin"] })
                .AddResource("private-resource", "Private resource", accessPolicy: new AccessPolicy { AllowedRoles = ["parcel-admin"] })
                .AddStorageBinding("private-binding", "private-resource", "features", storageLayerId: 0)
                .AddPublication("private-publication", "content-private", "private-resource", layerIndex: 0,
                    storageBindingId: "private-binding", publicationType: MetadataV2LayerType.EsriFeatureLayer)
                .Build();
            graph = graph with
            {
                Services = graph.Services.Select(service => service with
                { Metadata = service.Metadata with { Publisher = "alice" } }).ToArray()
            };
            return graph;
        }
    }
}

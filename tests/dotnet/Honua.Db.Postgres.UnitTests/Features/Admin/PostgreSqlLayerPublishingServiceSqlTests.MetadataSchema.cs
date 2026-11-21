// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Reflection;
using Honua.Core.Features.Admin.Abstractions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Db.Postgres.Features.Admin;
using Honua.Db.Postgres.Features.Infrastructure;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Honua.Db.Postgres.Tests.Features.Admin;

public sealed partial class PostgreSqlLayerPublishingServiceSqlTests
{
    [UnitTest]
    public void LayerRegistrySql_UsesConfiguredMetadataSchema()
    {
        var schemaConfig = new PostgresSchemaConfiguration(
            "custom_meta",
            PostgresSchemaConfiguration.DefaultDataSchema,
            [PostgresSchemaConfiguration.DefaultDataSchema, "public"]);
        var service = new PostgreSqlLayerPublishingService(
            Mock.Of<ITableDiscoveryService>(),
            Mock.Of<IMetadataV2GraphStore>(),
            NullLogger<PostgreSqlLayerPublishingService>.Instance,
            schemaConfiguration: schemaConfig);

        var serviceType = typeof(PostgreSqlLayerPublishingService);
        var predicate = (string)serviceType
            .GetMethod("BuildPublishedLayerConnectionPredicate", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(service, null)!;
        var pageSql = (string)serviceType
            .GetMethod("BuildPublishedLayerPageSql", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(service, null)!;
        var rewriteSql = PostgreSqlLayerPublishingService.BuildRewriteFieldTypePolicySql("custom_meta");

        predicate.Should().Contain("\"custom_meta\".\"services\"");
        predicate.Should().NotContain("honua.services");

        pageSql.Should().Contain("FROM \"custom_meta\".\"layers\" l");
        pageSql.Should().Contain("INNER JOIN \"custom_meta\".\"service_layers\" sl");
        pageSql.Should().Contain("LEFT JOIN \"custom_meta\".\"layer_fields\" f");
        pageSql.Should().NotContain("honua.layers");
        pageSql.Should().NotContain("honua.service_layers");
        pageSql.Should().NotContain("honua.layer_fields");

        rewriteSql.Should().Contain("FROM \"custom_meta\".\"layer_fields\"");
        rewriteSql.Should().NotContain("honua.layer_fields");
    }

    [UnitTest]
    public void LayerRegistrySql_MetadataSchemaParameterFallback_UsesTrimmedSchema()
    {
        var service = new PostgreSqlLayerPublishingService(
            Mock.Of<ITableDiscoveryService>(),
            Mock.Of<IMetadataV2GraphStore>(),
            NullLogger<PostgreSqlLayerPublishingService>.Instance,
            metadataSchema: "  alt_catalog  ");

        var pageSql = (string)typeof(PostgreSqlLayerPublishingService)
            .GetMethod("BuildPublishedLayerPageSql", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(service, null)!;

        pageSql.Should().Contain("FROM \"alt_catalog\".\"layers\" l");
        pageSql.Should().NotContain("honua.layers");
    }
}

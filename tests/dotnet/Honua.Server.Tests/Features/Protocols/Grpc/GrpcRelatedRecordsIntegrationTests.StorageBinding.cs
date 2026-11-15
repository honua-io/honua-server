// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Grpc.Core;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Infrastructure;
using Proto = Geospatial.V1;

namespace Honua.Server.Tests.Features.Protocols.Grpc;

public sealed partial class GrpcRelatedRecordsIntegrationTests
{
    [IntegrationTest]
    [Endpoint("POST /geospatial.v1.FeatureService/QueryRelatedRecords")]
    [InterfaceOperation(TestProtocols.Grpc, "geospatial.v1.FeatureService/QueryRelatedRecords")]
    public async Task RelatedRecords_ExternalTableWithManagedColumns_RejectsManagedStoreFallback()
    {
        var externalSchema = $"related_external_{Guid.NewGuid():N}";
        await _fixture.Postgres.CreateSchemaUnderLockAsync(externalSchema);
        try
        {
            await _fixture.Postgres.ExecuteDdlUnderLockAsync(
                $"CREATE TABLE {externalSchema}.features (LIKE \"{_fixture.CurrentSchema}\".features INCLUDING ALL)");
            var snapshot = _fixture.GetCurrentV2GraphSnapshot();
            var graph = snapshot.Graph;
            var bindingIds = graph.Resources.Single(resource => resource.Metadata.Id ==
                graph.Publications.First(publication => publication.LayerIndex == WebAppFixture.TestLayerId).ResourceId)
                .StorageBindingIds.ToHashSet(StringComparer.Ordinal);
            var provider = (TestMetadataV2GraphProvider)_fixture.GetService<IMetadataV2GraphProvider>();
            provider.SetGraph(graph with
            {
                Revision = graph.Revision + 1,
                StorageBindings = graph.StorageBindings.Select(binding => bindingIds.Contains(binding.Metadata.Id)
                    ? binding with { Locator = $"{externalSchema}.features" } : binding).ToArray()
            }, schema: _fixture.CurrentSchema);

            Func<Task> query = async () => await _client!.QueryRelatedRecordsAsync(new Proto.QueryRelatedRecordsRequest
            {
                ServiceId = WebAppFixture.TestServiceId, LayerId = WebAppFixture.TestLayerId,
                RelationshipId = 77, ObjectIds = { 1L }, ReturnGeometry = false
            }, _headers);

            var failure = await query.Should().ThrowAsync<RpcException>();
            failure.Which.StatusCode.Should().Be(StatusCode.FailedPrecondition);
        }
        finally
        {
            await _fixture.Postgres.DropSchemaAsync(externalSchema);
        }
    }
}

// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Admin.Abstractions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.TestKit.Infrastructure;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Server.Features.Admin.Models;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Admin;

public sealed partial class LayerPublishingIntegrationTests
{
    [IntegrationTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /api/v1/admin/connections/{id}/layers")]
    public async Task LinkExistingLayer_RepublishedHiddenPublications_PreservesAdvertising()
    {
        var layer = await PublishLayerAsync(new PublishLayerRequest
        {
            Schema = _schema, Table = _tableName, LayerName = "Parcel boundaries", ServiceName = _serviceName,
            GeometryColumn = "geom", GeometryType = "Point", Srid = 4326, PrimaryKey = "id", Fields = _idNamePopulationFields
        });
        _layerId = layer.LayerId;
        var snapshot = _fixture.GetCurrentV2GraphSnapshot();
        var publications = snapshot.Graph.Publications.Where(publication => publication.LayerIndex == layer.LayerId).ToArray();
        Assert.NotEmpty(publications);
        Assert.All(publications, publication => Assert.True(publication.Advertised));
        var ids = publications.Select(publication => publication.Metadata.Id).ToHashSet(StringComparer.Ordinal);
        var hiddenRevision = snapshot.Graph.Revision + 1;
        await SaveMetadataGraphAsync(snapshot.Graph with
        {
            Revision = hiddenRevision,
            Publications = snapshot.Graph.Publications.Select(publication => ids.Contains(publication.Metadata.Id)
                ? publication with { Advertised = false } : publication).ToArray()
        });

        var publishing = _fixture.GetService<ILayerPublishingService>();
        var republished = await publishing.LinkExistingLayerToServiceAsync(_fixture.Postgres.ConnectionString,
            layer.LayerId, _serviceName, true, _connectionId);

        Assert.NotNull(republished);
        var provider = (TestMetadataV2GraphProvider)_fixture.GetService<IMetadataV2GraphProvider>();
        var committed = new[] { provider.GetCurrentForSchema(string.Empty), provider.GetCurrentForSchema(_fixture.CurrentSchema) }
            .Where(current => current.Graph.Revision > hiddenRevision).ToArray();
        Assert.NotEmpty(committed);
        Assert.All(committed, current => Assert.All(ids, id => Assert.False(current.Index.PublicationsById[id].Advertised)));
    }
}

// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Scene.Domain;
using Honua.Core.Features.Security.Domain;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.Metadata.Domain.V2;

public sealed class MetadataV2PropertyRobustnessTests
{
    public static TheoryData<Type> GraphRecords() => new()
    {
        typeof(MetadataV2Graph), typeof(MetadataV2Resource), typeof(MetadataV2ResourceDisplay),
        typeof(MetadataV2ResourceEditing), typeof(MetadataV2ResourceStyle), typeof(MetadataV2StyleEncoding),
        typeof(MetadataV2Connection), typeof(MetadataV2StorageBinding), typeof(MetadataV2Service),
        typeof(MetadataV2ServiceSettings), typeof(MetadataV2Publication), typeof(MetadataV2Catalog),
        typeof(MetadataV2ProjectionProfile), typeof(MetadataV2Policy), typeof(MetadataV2Role),
        typeof(MetadataV2RuntimeSnapshot), typeof(MetadataV2ObjectMetadata), typeof(MetadataV2Status),
        typeof(MetadataV2Condition), typeof(MetadataV2ExtensionPoint), typeof(MetadataV2ContactPoint),
        typeof(MetadataV2Link), typeof(MetadataV2Field), typeof(MetadataV2FieldDomain),
        typeof(MetadataV2CodedValue), typeof(MetadataV2Subtypes), typeof(MetadataV2Subtype),
        typeof(MetadataV2SubtypeFieldOverride), typeof(MetadataV2AttributeRule),
        typeof(MetadataV2ContingentValueGroup), typeof(MetadataV2ContingentValue),
        typeof(MetadataV2ContingentFieldValue), typeof(MetadataV2OwnerEditPolicy),
        typeof(MetadataV2Relationship), typeof(MetadataV2PublicationIdentifier),
        typeof(MetadataV2SpatialReference), typeof(MetadataV2Bbox), typeof(MetadataV2ResourceSpatial),
        typeof(MetadataV2TimeRange), typeof(MetadataV2ResourceTemporal), typeof(MetadataV2PermanentFilter),
        typeof(MetadataV2ExtrusionInfo), typeof(MetadataV2Folder), typeof(MetadataV2ServicePathAlias),
        typeof(AccessPolicy), typeof(Symbology3D), typeof(Symbology3DRule), typeof(Symbology3DColor)
    };

    [UnitTheory]
    [MemberData(nameof(GraphRecords))]
    public void Serialize_GraphRecordWithFutureProperties_RetainsJsonValues(Type recordType)
    {
        const string json = """
            {"code":0,"attribute":"height","futureNull":null,
             "futureField":{"values":[1,null,{"enabled":true}],"label":"retained"}}
            """;
        var typeInfo = MetadataV2JsonContext.Default.GetTypeInfo(recordType)!;
        typeInfo.Should().NotBeNull();
        var value = JsonSerializer.Deserialize(json, typeInfo);
        using var serialized = JsonDocument.Parse(JsonSerializer.Serialize(value, typeInfo));
        using var original = JsonDocument.Parse(json);

        JsonElement.DeepEquals(serialized.RootElement.GetProperty("futureField"),
            original.RootElement.GetProperty("futureField")).Should().BeTrue(recordType.Name);
        serialized.RootElement.GetProperty("futureNull").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [UnitTest]
    public void Serialize_KnownFieldEdit_RetainsNestedFutureProperties()
    {
        const string json = """
            {"futureRoot":1,"resources":[{"futureDataset":2,
              "metadata":{"id":"dataset","futureMetadata":3},
              "status":{"futureStatus":4,"conditions":[{"type":"Ready","futureCondition":5}]}}]}
            """;
        var graph = JsonSerializer.Deserialize(json, MetadataV2JsonContext.Default.MetadataV2Graph)!;
        var dataset = graph.Resources.Single();
        var edited = graph with
        {
            Resources = [dataset with { Metadata = dataset.Metadata with { Title = "Edited title" } }]
        };
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(edited,
            MetadataV2JsonContext.Default.MetadataV2Graph));
        var persisted = document.RootElement.GetProperty("resources")[0];

        document.RootElement.GetProperty("futureRoot").GetInt32().Should().Be(1);
        persisted.GetProperty("futureDataset").GetInt32().Should().Be(2);
        persisted.GetProperty("metadata").GetProperty("futureMetadata").GetInt32().Should().Be(3);
        persisted.GetProperty("metadata").GetProperty("title").GetString().Should().Be("Edited title");
        persisted.GetProperty("status").GetProperty("futureStatus").GetInt32().Should().Be(4);
        persisted.GetProperty("status").GetProperty("conditions")[0]
            .GetProperty("futureCondition").GetInt32().Should().Be(5);
    }

    [UnitTest]
    public void Reconcile_KnownFolderRename_PreservesFutureProperties()
    {
        const string json = """
            {"folders":[{"metadata":{"id":"folder","name":"before","futureMetadata":1},
              "futureFolder":{"value":2}}]}
            """;
        var graph = JsonSerializer.Deserialize(json, MetadataV2JsonContext.Default.MetadataV2Graph)!;
        var folder = graph.Folders.Single();
        var renamed = graph with
        {
            Folders = [folder with { Metadata = folder.Metadata with { Name = "renamed" } }]
        };
        var reconciled = MetadataV2PathReservations.Reconcile(graph, renamed);
        using var serialized = JsonDocument.Parse(JsonSerializer.Serialize(reconciled,
            MetadataV2JsonContext.Default.MetadataV2Graph));
        var persisted = serialized.RootElement.GetProperty("folders")[0];

        reconciled.Folders.Single().PreviousNames.Should().Contain("before");
        persisted.GetProperty("futureFolder").GetProperty("value").GetInt32().Should().Be(2);
        persisted.GetProperty("metadata").GetProperty("futureMetadata").GetInt32().Should().Be(1);
        persisted.GetProperty("metadata").GetProperty("name").GetString().Should().Be("renamed");
    }
}

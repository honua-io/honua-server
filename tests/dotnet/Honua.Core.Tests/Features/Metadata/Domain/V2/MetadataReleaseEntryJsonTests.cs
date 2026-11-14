// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Core.Tests.Features.Metadata.Domain.V2;

[Protocol(ProtocolNames.TestQuality)]
[Operation(Operations.Metadata)]
public sealed class MetadataReleaseEntryJsonTests
{
    private static readonly string[] SharedTargetProperties =
    [
        "semanticId",
        "artifactKind",
        "resourceType",
        "sourceField",
        "desiredMetadataRevision",
        "desiredContentVersionId",
        "desiredProvenance",
    ];

    private static readonly MetadataBoundFieldSummary SourceField = new()
    {
        SemanticId = "res.parcels.field.name",
        ParentResourceId = "res-parcels",
        FieldName = "name",
    };

    [UnitTest]
    public void Serialize_ReleaseEntry_WritesSharedTargetPropertiesFirst()
    {
        var entry = new MetadataReleaseEntry
        {
            SemanticId = "res.parcels",
            ArtifactKind = MetadataSemanticArtifactKind.Resource,
            ResourceType = MetadataV2ObjectType.FeatureDataset,
            SourceField = SourceField,
            DesiredMetadataRevision = 41,
            DesiredContentVersionId = "content-41",
        };

        var json = JsonSerializer.Serialize(entry, MetadataReleaseJsonContext.Default.MetadataReleaseEntry);

        string[] expected = [.. SharedTargetProperties, "changeClass", "targetStates", "dependentSemanticIds", "status"];
        Assert.Equal(expected, PropertyNames(json));
    }

    [UnitTest]
    public void Serialize_GitOpsReleaseEntry_WritesSharedTargetPropertiesFirst()
    {
        var entry = new GitOpsMetadataReleaseEntry
        {
            SemanticId = "res.parcels",
            ArtifactKind = MetadataSemanticArtifactKind.Resource,
            ResourceType = MetadataV2ObjectType.FeatureDataset,
            SourceField = SourceField,
            DesiredMetadataRevision = 41,
            DesiredContentVersionId = "content-41",
            ChangeClass = MetadataReleaseChangeClass.Metadata,
        };

        var json = JsonSerializer.Serialize(entry, MetadataReleaseJsonContext.Default.GitOpsMetadataReleaseEntry);

        string[] expected = [.. SharedTargetProperties, "changeClass", "targetStates", "dependentSemanticIds"];
        Assert.Equal(expected, PropertyNames(json));
    }

    private static string[] PropertyNames(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.EnumerateObject().Select(static property => property.Name).ToArray();
    }
}

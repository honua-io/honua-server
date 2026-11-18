// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Nodes;
using FluentAssertions;
using Honua.Server.Features.Admin.PairedPublications;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Admin;

public sealed partial class PairedPublishEndpointTests
{
    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/tile-operations/jobs")]
    public async Task PairedPublish_Layer_WritesCompanionFilesThatDescribeOnlyPublicFields()
    {
        HideField(HiddenField);

        var published = await PublishAsync(IdentityField);

        var readme = await ReadTextAsync(published, ExportCompanionFiles.ReadmeFileName);
        var agents = await ReadTextAsync(published, ExportCompanionFiles.AgentsFileName);
        var style = JsonNode.Parse(await ReadTextAsync(published, ExportCompanionFiles.StyleFileName))!;
        var item = JsonNode.Parse(await ReadTextAsync(published, PairedPublicationDescriptorFactory.StacItemFileName))!;
        readme.Should().Contain($"`{_publicationId}.parquet`").And.NotContain($"`{HiddenField}`");
        agents.Should().Contain($"| `{IdentityField}` |").And.NotContain($"`{HiddenField}`");
        style["sources"]![ExportStyleAsset.SourceId]!["url"]!.GetValue<string>().Should().Be($"pmtiles://{_publicationId}.pmtiles");
        style["layers"]!.AsArray().Should().NotBeEmpty()
            .And.OnlyContain(layer => layer!["source-layer"]!.GetValue<string>() == "layer");
        item["properties"]!["table:columns"]!.AsArray().Select(column => column!["name"]!.GetValue<string>())
            .Should().Contain([IdentityField, "geometry"]).And.NotContain(HiddenField);
        item["properties"]!["providers"]!.AsArray().Should().ContainSingle(provider => provider!["roles"]![0]!.GetValue<string>() == "host");
        item["assets"]!["readme"]!["href"]!.GetValue<string>().Should().Be(ExportCompanionFiles.ReadmeFileName);
        item["assets"]!["thumbnail"]!["href"]!.GetValue<string>().Should().Be(ExportThumbnail.FileName);
        using var thumbnail = SkiaSharp.SKBitmap.Decode(await ReadBytesAsync(ArtifactKey(published, ExportThumbnail.FileName)));
        thumbnail.Width.Should().Be(ExportThumbnail.Size);
    }

    private async Task<string> ReadTextAsync(Honua.Core.Features.Publishing.Descriptors.PublishedPublication published, string fileName)
        => System.Text.Encoding.UTF8.GetString(await ReadBytesAsync(ArtifactKey(published, fileName)));
}

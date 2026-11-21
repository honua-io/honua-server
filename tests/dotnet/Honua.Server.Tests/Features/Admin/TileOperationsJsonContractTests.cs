// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Tiles.PMTiles;
using Honua.Infrastructure.Progress;
using Honua.Server.Features.Admin.TileOperations;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Admin;

// Tile-operation responses use camelCase keys, as the JS SDK lifecycle client reads them by exact name.
public sealed class TileOperationsJsonContractTests
{
    [UnitTest]
    public void StartResponse_UsesCamelCaseKeys()
    {
        var json = JsonSerializer.Serialize(
            new TileOperationStartResponse
            {
                JobId = "job-1",
                Message = "Tile operation queued.",
                StatusUrl = "/api/v1/admin/tile-operations/jobs/job-1",
                CancelUrl = "/api/v1/admin/tile-operations/jobs/job-1/cancel"
            },
            TileOperationsJsonContext.Default.TileOperationStartResponse);

        PropertyNames(json).Should().Equal("jobId", "message", "statusUrl", "cancelUrl");
    }

    [UnitTest]
    public void Progress_UsesCamelCaseKeysAndNumericStatus()
    {
        var json = JsonSerializer.Serialize(CompletedPublish(), TileOperationsJsonContext.Default.TileOperationProgress);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        root.GetProperty("jobId").GetString().Should().Be("job-1");
        root.GetProperty("status").GetInt32().Should().Be((int)OperationStatus.Completed);
        root.GetProperty("totalTiles").GetInt64().Should().Be(4);
        var artifact = root.GetProperty("publishedArtifact");
        artifact.GetProperty("accessUrl").GetString().Should().Be("https://tiles.example/a.pmtiles");
        artifact.GetProperty("urlStrategy").GetString().Should().Be("PublicUrl");
        artifact.GetProperty("storageProvider").GetString().Should().Be("Local");
        PropertyNames(json).Should().OnlyContain(name => char.IsLower(name[0]));
    }

    private static TileOperationProgress CompletedPublish() => TileOperationProgress.CreateInitial(
            "job-1", "publish", "svc", 1, "WebMercatorQuad") with
    {
        Status = OperationStatus.Completed,
        TotalTiles = 4,
        ProcessedTiles = 4,
        SuccessfulTiles = 4,
        PublishedArtifact = new PMTilesArtifactDescriptor
        {
            ArtifactId = "a",
            StorageProvider = CloudStorageProvider.Local,
            Bucket = "local",
            ObjectKey = "a.pmtiles",
            ContentType = "application/vnd.pmtiles",
            SizeBytes = 624,
            UrlStrategy = PMTilesUrlStrategy.PublicUrl,
            AccessUrl = "https://tiles.example/a.pmtiles",
            PublishedAt = DateTimeOffset.UnixEpoch,
            MinZoom = 0,
            MaxZoom = 2
        }
    };

    private static string[] PropertyNames(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.EnumerateObject().Select(property => property.Name).ToArray();
    }
}

// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Geoprocessing.Raster;

namespace Honua.Core.Tests.Features.Geoprocessing;

public sealed class RasterOutputContractTests
{
    [Fact]
    public void ObjectOutput_RoundTripsThroughSourceGeneratedContract()
    {
        RasterOutputDescriptor expected = ObjectOutput();

        var json = RasterOutputJson.Serialize(expected);
        var actual = RasterOutputJson.Deserialize(json);

        Assert.Equal(json, RasterOutputJson.Serialize(actual));
        Assert.DoesNotContain("https://", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("credential", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PublicationManifest_RoundTripsMetadataOnlyAndRejectsAttemptMismatch()
    {
        var manifest = new RasterOutputPublicationManifest
        {
            JobId = "job-42",
            Attempt = 2,
            CreatedAt = DateTimeOffset.Parse("2026-08-01T00:30:00Z", CultureInfo.InvariantCulture),
            Outputs = [Stage()]
        };

        var json = RasterOutputJson.SerializeManifest(manifest);
        var actual = RasterOutputJson.DeserializeManifest(json);
        var invalid = manifest with
        {
            Outputs = [Stage() with { Attempt = 3, Lineage = Lineage() with { Attempt = 3 } }]
        };

        Assert.Equal(json, RasterOutputJson.SerializeManifest(actual));
        Assert.True(RasterOutputDescriptorValidator.Validate(manifest).IsValid);
        Assert.Contains(RasterOutputDescriptorValidator.Validate(invalid).Errors,
            error => error.Field == "outputs");
        Assert.DoesNotContain("payload", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("https://", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validator_RejectsSignedUrlsAndInlineChecksumMismatch()
    {
        var unsafeObject = ObjectOutput() with
        {
            ObjectKey = "https://bucket.example/output.tif?X-Amz-Signature=secret"
        };
        var inline = new InlineRasterOutputDescriptor
        {
            ArtifactId = "rast_inline",
            OutputName = "preview",
            Payload = [1, 2, 3],
            Content = Content(4, new string('0', 64)),
            Grid = Grid(),
            Engine = Engine(),
            Lineage = Lineage(),
            Retention = Retention()
        };

        var unsafeResult = RasterOutputDescriptorValidator.Validate(unsafeObject);
        var inlineResult = RasterOutputDescriptorValidator.Validate(inline);

        Assert.Contains(unsafeResult.Errors, error => error.Code == RasterOutputValidationCodes.UnsafeLocator);
        Assert.Contains(inlineResult.Errors, error => error.Code == RasterOutputValidationCodes.ChecksumMismatch);
    }

    [Fact]
    public void StableIdentity_IsRetrySafeAndAttemptStagingRemainsIsolated()
    {
        var checksum = new RasterChecksum("sha256", new string('a', 64));

        var firstArtifact = RasterOutputIdentity.CreateArtifactId("job-42", "elevation", checksum);
        var retryArtifact = RasterOutputIdentity.CreateArtifactId("job-42", "elevation", checksum);
        var firstStage = RasterOutputWorkerContract.BuildStagingObjectKey("job-42", 0, "elevation");
        var retryStage = RasterOutputWorkerContract.BuildStagingObjectKey("job-42", 1, "elevation");

        Assert.Equal(firstArtifact, retryArtifact);
        Assert.NotEqual(firstStage, retryStage);
        Assert.DoesNotContain("?", firstStage, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", firstStage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ManifestKey_UsesNamespaceDisjointFromCallerOutputNames()
    {
        var callerOutput = RasterOutputWorkerContract.BuildStagingObjectKey(
            "job-42",
            2,
            "publication-manifest.json");
        var manifest = RasterOutputWorkerContract.BuildManifestObjectKey("job-42", 2);

        Assert.NotEqual(callerOutput, manifest);
        Assert.Contains("/_honua/publication-manifest.json", manifest, StringComparison.Ordinal);
    }

    [Fact]
    public void ManifestKeyParser_AcceptsOnlyExactDerivedAttemptKeys()
    {
        var key = RasterOutputWorkerContract.BuildManifestObjectKey("job-42", 12);

        Assert.True(RasterOutputWorkerContract.TryParseManifestObjectKey(
            key,
            out var jobId,
            out var attempt));
        Assert.Equal("job-42", jobId);
        Assert.Equal(12, attempt);
        Assert.False(RasterOutputWorkerContract.TryParseManifestObjectKey(
            "raster/staging/job-42/attempt-12/result.tif",
            out _,
            out _));
        Assert.False(RasterOutputWorkerContract.TryParseManifestObjectKey(
            "raster/staging/other/attempt-12/_honua/publication-manifest.json/extra",
            out _,
            out _));
    }

    [Fact]
    public void StageValidator_RequiresStrongContentAndMatchingLineage()
    {
        var stage = Stage() with
        {
            Content = Content(32, checksum: null),
            Lineage = Lineage() with { JobId = "other-job" }
        };

        var result = RasterOutputDescriptorValidator.Validate(stage);

        Assert.Contains(result.Errors, error => error.Field == "content.checksum");
        Assert.Contains(result.Errors, error => error.Field == "lineage.jobId");
    }

    [Fact]
    public void StageValidator_RequiresExactAttemptKeyAndLogicalStoreReference()
    {
        var stage = Stage() with
        {
            StoreReference = "https://bucket.example/results?token=secret",
            ObjectKey = RasterOutputWorkerContract.BuildStagingObjectKey(
                "job-42",
                3,
                "elevation")
        };

        var result = RasterOutputDescriptorValidator.Validate(stage);

        Assert.Contains(result.Errors, error => error.Field == "storeReference"
            && error.Code == RasterOutputValidationCodes.UnsafeLocator);
        Assert.Contains(result.Errors, error => error.Field == "objectKey"
            && error.Code == RasterOutputValidationCodes.InvalidField);
    }

    [Fact]
    public void InlineValidator_EnforcesAbsoluteCeilingEvenWhenOptionIsLarger()
    {
        var payload = new byte[RasterOutputContract.MaximumInlineBytes + 1];
        var checksum = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(payload));
        var inline = new InlineRasterOutputDescriptor
        {
            ArtifactId = "rast_inline",
            OutputName = "preview",
            Payload = payload,
            Content = Content(payload.LongLength, checksum),
            Grid = Grid(),
            Engine = Engine(),
            Lineage = Lineage(),
            Retention = Retention()
        };

        var result = RasterOutputDescriptorValidator.Validate(
            inline,
            new RasterOutputValidationOptions { MaxInlineBytes = int.MaxValue });

        Assert.Contains(result.Errors, error =>
            error.Code == RasterOutputValidationCodes.InlinePayloadTooLarge);
    }

    [Fact]
    public void CreateArtifactId_RejectsWeakOrMalformedChecksums()
    {
        Assert.Throws<ArgumentException>(() => RasterOutputIdentity.CreateArtifactId(
            "job-42",
            "elevation",
            new RasterChecksum("md5", new string('a', 32))));
        Assert.Throws<ArgumentException>(() => RasterOutputIdentity.CreateArtifactId(
            "job-42",
            "elevation",
            new RasterChecksum("sha256", "not-hex")));
    }

    internal static StagedRasterOutputDescriptor Stage() => new()
    {
        JobId = "job-42",
        Attempt = 2,
        OutputName = "elevation",
        StoreReference = "gp-results",
        ObjectKey = RasterOutputWorkerContract.BuildStagingObjectKey("job-42", 2, "elevation"),
        Content = Content(32, new string('a', 64)),
        Encoding = RasterOutputEncoding.CloudOptimizedGeoTiff,
        Grid = Grid(),
        Engine = Engine(),
        Lineage = Lineage() with { Attempt = 2 },
        CreatedAt = DateTimeOffset.Parse("2026-08-01T00:00:00Z", CultureInfo.InvariantCulture),
        ExpiresAt = DateTimeOffset.Parse("2026-08-02T00:00:00Z", CultureInfo.InvariantCulture)
    };

    internal static ObjectStoreRasterOutputDescriptor ObjectOutput() => new()
    {
        ArtifactId = "rast_0123456789abcdef",
        OutputName = "elevation",
        StoreReference = "gp-results",
        ObjectKey = "raster/published/01/rast_0123456789abcdef.tif",
        ObjectVersion = "sha256:aaaaaaaa",
        Encoding = RasterOutputEncoding.CloudOptimizedGeoTiff,
        Content = Content(32, new string('a', 64)),
        Grid = Grid(),
        Engine = Engine(),
        Lineage = Lineage(),
        Retention = Retention()
    };

    internal static RasterContentIdentity Content(long size, string? checksum) => new()
    {
        SizeBytes = size,
        MediaType = "image/tiff",
        Checksum = checksum is null ? null : new RasterChecksum("sha256", checksum)
    };

    internal static RasterGridMetadata Grid() => new()
    {
        Crs = "EPSG:4326",
        Width = 4,
        Height = 4,
        BandCount = 2,
        GeoTransform = [0, 1, 0, 4, 0, -1]
    };

    internal static RasterProducingEngine Engine() => new("gdal", "3.11.0");

    internal static RasterOutputLineage Lineage() => new()
    {
        JobId = "job-42",
        Attempt = 0,
        ProcessId = "raster.reproject",
        SourceArtifactIds = ["source-1"]
    };

    internal static RasterOutputRetention Retention() => new(
        DateTimeOffset.Parse("2026-08-01T00:00:00Z", CultureInfo.InvariantCulture),
        DateTimeOffset.Parse("2026-08-08T00:00:00Z", CultureInfo.InvariantCulture));
}

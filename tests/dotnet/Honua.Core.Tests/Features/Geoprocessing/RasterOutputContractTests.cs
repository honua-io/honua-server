// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Cryptography;
using FluentAssertions;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Geoprocessing.Raster;
using Honua.Core.Features.Infrastructure.Domain;
using Xunit;

namespace Honua.Core.Tests.Features.Geoprocessing;

/// <summary>
/// Contract tests for the typed raster output descriptors (#3089): serialization
/// round-trips, envelope detection over durable artifact-reference strings, bounded
/// validation, fail-closed Zarr rejection, and the attempt-scoped key scheme.
/// </summary>
public sealed class RasterOutputContractTests
{
    [Fact]
    public void Serialize_StagedObjectDescriptor_RoundTrips()
    {
        var descriptor = CreateStagedDescriptor();

        var json = RasterOutputJson.Serialize(descriptor);
        var parsed = RasterOutputJson.Deserialize(json);

        parsed.Should().BeOfType<StagedObjectRasterOutputDescriptor>();
        parsed.Should().BeEquivalentTo(descriptor);
    }

    [Fact]
    public void Serialize_InlineDescriptor_RoundTripsPayload()
    {
        var payload = new byte[] { 1, 2, 3, 4, 5 };
        var descriptor = CreateInlineDescriptor(payload);

        var parsed = RasterOutputJson.Deserialize(RasterOutputJson.Serialize(descriptor));

        parsed.Should().BeOfType<InlineRasterOutputDescriptor>()
            .Which.Payload.Should().Equal(payload);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("data:image/tiff;base64,AAAA")]
    [InlineData("https://example.com/object.tif")]
    [InlineData("{\"not\":\"a descriptor\"}")]
    [InlineData("{\"outputType\":\"unknown-kind\"}")]
    public void TryDeserialize_NonDescriptorReference_ReturnsFalse(string? reference)
    {
        RasterOutputJson.TryDeserialize(reference, out var descriptor).Should().BeFalse();
        descriptor.Should().BeNull();
    }

    [Fact]
    public void TryDeserialize_PublishedDescriptor_ReturnsTypedDescriptor()
    {
        var json = RasterOutputJson.Serialize(CreateStagedDescriptor());

        RasterOutputJson.TryDeserialize(json, out var descriptor).Should().BeTrue();
        descriptor.Should().BeOfType<StagedObjectRasterOutputDescriptor>();
    }

    [Fact]
    public void Validate_WellFormedStagedDescriptor_Passes()
    {
        var result = RasterOutputDescriptorValidator.Validate(CreateStagedDescriptor());

        result.IsValid.Should().BeTrue(string.Join("; ", result.Errors.Select(e => e.Message)));
    }

    [Theory]
    [InlineData("application/vnd.zarr", "gp/outputs/job-1/a1/output1/output.bin")]
    [InlineData("image/tiff", "gp/outputs/job-1/a1/output1/result.zarr")]
    [InlineData("image/tiff", "gp/outputs/job-1/a1/output1/store.zarr/0.0.0")]
    public void Validate_ZarrShapedStagedOutput_FailsClosed(string mediaType, string objectKey)
    {
        var descriptor = CreateStagedDescriptor() with
        {
            Content = CreateContent(mediaType),
            ObjectKey = objectKey,
        };

        var result = RasterOutputDescriptorValidator.Validate(descriptor);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(error =>
            error.Code == RasterOutputValidationCodes.ZarrOutputUnsupported);
    }

    [Fact]
    public void Validate_InlinePayloadAboveCeiling_IsRejected()
    {
        var payload = new byte[8 * 1024];
        var descriptor = CreateInlineDescriptor(payload);

        var result = RasterOutputDescriptorValidator.Validate(
            descriptor,
            new RasterOutputValidationOptions { MaxInlineBytes = 4 * 1024 });

        result.Errors.Should().Contain(error =>
            error.Code == RasterOutputValidationCodes.InlinePayloadTooLarge);
    }

    [Fact]
    public void Validate_InlineChecksumMismatch_IsRejected()
    {
        var descriptor = CreateInlineDescriptor(new byte[] { 1, 2, 3 }) with
        {
            Content = new RasterContentIdentity
            {
                SizeBytes = 3,
                MediaType = "image/tiff",
                Checksum = new RasterChecksum("sha256", new string('0', 64)),
            },
        };

        var result = RasterOutputDescriptorValidator.Validate(descriptor);

        result.Errors.Should().Contain(error =>
            error.Code == RasterOutputValidationCodes.ChecksumMismatch);
    }

    [Fact]
    public void Validate_MissingChecksum_IsRejected()
    {
        var descriptor = CreateStagedDescriptor() with
        {
            Content = new RasterContentIdentity { SizeBytes = 10, MediaType = "image/tiff" },
        };

        var result = RasterOutputDescriptorValidator.Validate(descriptor);

        result.Errors.Should().Contain(error =>
            error.Code == RasterOutputValidationCodes.InvalidContentIdentity);
    }

    [Theory]
    [InlineData("/rooted/key.tif")]
    [InlineData("../escape.tif")]
    [InlineData("a/../b.tif")]
    [InlineData("https://host/key.tif")]
    public void Validate_UnsafeObjectKey_IsRejected(string objectKey)
    {
        var descriptor = CreateStagedDescriptor() with { ObjectKey = objectKey };

        var result = RasterOutputDescriptorValidator.Validate(descriptor);

        result.Errors.Should().Contain(error =>
            error.Code == RasterOutputValidationCodes.UnsafeLocator);
    }

    [Fact]
    public void ObjectKeys_BuildAndParse_RoundTripAttemptIdentity()
    {
        var key = GeoprocessingOutputObjectKeys.Build("gp/outputs", "job-42", 3, "outputRaster", "result.tif");

        key.Should().Be("gp/outputs/job-42/a3/outputRaster/result.tif");
        GeoprocessingOutputObjectKeys.TryParse("gp/outputs", key, out var jobId, out var attempt).Should().BeTrue();
        jobId.Should().Be("job-42");
        attempt.Should().Be(3);
    }

    [Theory]
    [InlineData("unrelated/key.tif")]
    [InlineData("gp/outputs/job-1/notattempt/output/file.tif")]
    [InlineData("gp/outputs/job-1/a0/output/file.tif")]
    [InlineData("gp/outputs/job-1/a1/file.tif")]
    public void ObjectKeys_TryParse_RejectsForeignKeys(string key)
    {
        GeoprocessingOutputObjectKeys.TryParse("gp/outputs", key, out _, out _).Should().BeFalse();
    }

    private static StagedObjectRasterOutputDescriptor CreateStagedDescriptor() => new()
    {
        JobId = "job-1",
        AttemptNumber = 1,
        OutputName = "output1",
        Content = CreateContent("image/tiff"),
        Grid = new RasterOutputGridSummary
        {
            Width = 512,
            Height = 256,
            BandCount = 3,
            BitsPerSample = 16,
            PixelScale = new RasterSourcePixelScale(10, 10),
            CoordinateReferenceSystem = "EPSG:3857",
        },
        ProducingEngine = RasterOutputContract.GdalWorkerEngine,
        Lineage = new RasterOutputLineage
        {
            ProcessId = "raster.resample",
            PlanId = "plan-1",
            SourceReferences = ["ObjectStoreCogRasterSourceDescriptor:v1"],
        },
        Provider = CloudStorageProvider.Local,
        StoreReference = "gp-outputs",
        ObjectKey = "gp/outputs/job-1/a1/output1/result.tif",
    };

    private static InlineRasterOutputDescriptor CreateInlineDescriptor(byte[] payload) => new()
    {
        JobId = "job-1",
        AttemptNumber = 1,
        OutputName = "output1",
        Content = new RasterContentIdentity
        {
            SizeBytes = payload.Length,
            MediaType = "image/tiff",
            Checksum = new RasterChecksum(
                "sha256",
                Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant()),
        },
        ProducingEngine = RasterOutputContract.GdalWorkerEngine,
        Payload = payload,
    };

    private static RasterContentIdentity CreateContent(string mediaType) => new()
    {
        SizeBytes = 10,
        MediaType = mediaType,
        Checksum = new RasterChecksum("sha256", new string('a', 64)),
    };
}

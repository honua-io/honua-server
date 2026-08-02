// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Geoprocessing.Raster;

namespace Honua.Core.Tests.Features.Geoprocessing;

/// <summary>
/// Contract tests for reference-based raster sources carried by durable GP jobs.
/// </summary>
public sealed class RasterSourceContractTests
{
    public static TheoryData<RasterSourceDescriptor> DescriptorCases =>
        new()
        {
            Postgis(),
            Cog(),
            Zarr(),
            Staged(),
            Inline([1, 2, 3, 4]),
        };

    [Theory]
    [MemberData(nameof(DescriptorCases))]
    public void Serialize_Descriptor_RoundTripsWithSourceGeneratedContract(RasterSourceDescriptor descriptor)
    {
        var json = RasterSourceJson.Serialize(descriptor);
        var roundTrip = RasterSourceJson.Deserialize(json);

        Assert.Equal(descriptor.GetType(), roundTrip.GetType());
        Assert.Equal(json, RasterSourceJson.Serialize(roundTrip));
        Assert.Contains("\"sourceType\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("credential", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Deserialize_SameVersionWithUnknownOptionalProperty_RemainsCompatible()
    {
        var json = RasterSourceJson.Serialize(Cog());
        var extended = json.Insert(json.Length - 1, ",\"futureOptionalHint\":true");

        var descriptor = RasterSourceJson.Deserialize(extended);

        Assert.IsType<ObjectStoreCogRasterSourceDescriptor>(descriptor);
    }

    [Fact]
    public void Validate_FutureContractVersion_IsRejectedForRollingUpgradeSafety()
    {
        var descriptor = Cog() with
        {
            SourceContractVersion = RasterSourceContract.CurrentVersion + 1,
        };

        var result = RasterSourceDescriptorValidator.Validate(descriptor);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Code == RasterSourceValidationCodes.UnsupportedContractVersion);
    }

    [Fact]
    public void Deserialize_UnknownSourceType_IsRejected()
    {
        const string json = """
            {
              "sourceType": "future-provider",
              "sourceContractVersion": 1,
              "version": "v1"
            }
            """;

        Assert.Throws<JsonException>(() => RasterSourceJson.Deserialize(json));
    }

    [Theory]
    [InlineData("../secret.tif")]
    [InlineData("/vsis3/private-bucket/secret.tif")]
    [InlineData("https://169.254.169.254/latest/meta-data")]
    [InlineData("tiles/source.tif?X-Amz-Credential=secret")]
    [InlineData("C:\\secrets\\source.tif")]
    [InlineData("bucket/%2e%2e/secret.tif")]
    public void Validate_ObjectStoreKeyInjection_IsRejected(string objectKey)
    {
        var result = RasterSourceDescriptorValidator.Validate(Cog() with { ObjectKey = objectKey });

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Code == RasterSourceValidationCodes.UnsafeLocator);
    }

    [Theory]
    [InlineData("https://example.com/context")]
    [InlineData("context?access_key=secret")]
    [InlineData("../../context")]
    [InlineData("tenant/context")]
    public void Validate_SecurityContextInjection_IsRejected(string contextReference)
    {
        var descriptor = Cog() with
        {
            SecurityContext = Security() with { AuthorizationSnapshotReference = contextReference },
        };

        var result = RasterSourceDescriptorValidator.Validate(descriptor);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Code == RasterSourceValidationCodes.InvalidSecurityContext);
    }

    [Fact]
    public void Validate_ObjectStoreReferenceWithoutImmutablePin_IsRejected()
    {
        var descriptor = Cog() with
        {
            Version = string.Empty,
            Content = Content() with { ETag = null, Checksum = null },
        };

        var result = RasterSourceDescriptorValidator.Validate(descriptor);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Code == RasterSourceValidationCodes.ImmutableIdentityRequired);
    }

    [Fact]
    public void Validate_InlinePayloadAboveConfiguredCeiling_IsRejected()
    {
        var descriptor = Inline(new byte[17]);
        var options = RasterSourceValidationOptions.Default with { MaxInlineBytes = 16 };

        var result = RasterSourceDescriptorValidator.Validate(descriptor, options);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Code == RasterSourceValidationCodes.InlinePayloadTooLarge);
    }

    [Fact]
    public void Validate_BoundedWindowBandsTimeAndDimensions_AreAccepted()
    {
        var descriptor = Zarr() with
        {
            Selection = new RasterSourceSelection
            {
                PixelWindow = new RasterPixelWindow(10, 20, 256, 256),
                Bands = [1, 3],
                Time = new RasterTimeSelection(
                    DateTimeOffset.Parse("2026-01-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture),
                    DateTimeOffset.Parse("2026-01-02T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture)),
                Dimensions = [new RasterDimensionSlice("level", 0, 4, 1)],
            },
        };

        var result = RasterSourceDescriptorValidator.Validate(descriptor);

        Assert.True(result.IsValid, string.Join("; ", result.Errors.Select(error => error.Message)));
    }

    [Fact]
    public async Task ResolveAsync_MetadataMatchesPinnedDescriptor_ReturnsAvailable()
    {
        var descriptor = Cog();
        var resolver = new StubMetadataResolver((_, _) => Task.FromResult(
            RasterSourceMetadataResolution.Available(new RasterSourceMetadata
            {
                Version = descriptor.Version,
                Content = descriptor.Content,
            })));

        var result = await RasterSourceMetadataAdmission.ResolveAsync(descriptor, resolver);

        Assert.Equal(RasterSourceMetadataStatus.Available, result.Status);
    }

    [Fact]
    public async Task ResolveAsync_ChecksumDoesNotMatch_ReturnsIntegrityMismatch()
    {
        var descriptor = Cog();
        var resolver = new StubMetadataResolver((_, _) => Task.FromResult(
            RasterSourceMetadataResolution.Available(new RasterSourceMetadata
            {
                Version = descriptor.Version,
                Content = descriptor.Content with
                {
                    Checksum = new RasterChecksum("sha256", new string('b', 64)),
                },
            })));

        var result = await RasterSourceMetadataAdmission.ResolveAsync(descriptor, resolver);

        Assert.Equal(RasterSourceMetadataStatus.IntegrityMismatch, result.Status);
    }

    [Fact]
    public async Task ResolveAsync_VersionDoesNotMatch_ReturnsStale()
    {
        var descriptor = Cog();
        var resolver = new StubMetadataResolver((_, _) => Task.FromResult(
            RasterSourceMetadataResolution.Available(new RasterSourceMetadata
            {
                Version = "version-2",
                Content = descriptor.Content,
            })));

        var result = await RasterSourceMetadataAdmission.ResolveAsync(descriptor, resolver);

        Assert.Equal(RasterSourceMetadataStatus.Stale, result.Status);
    }

    [Fact]
    public async Task ResolveAsync_MissingArtifact_PreservesMissingStatus()
    {
        var descriptor = Staged();
        var resolver = new StubMetadataResolver((_, _) => Task.FromResult(
            RasterSourceMetadataResolution.Failure(RasterSourceMetadataStatus.Missing, "artifact_missing")));

        var result = await RasterSourceMetadataAdmission.ResolveAsync(descriptor, resolver);

        Assert.Equal(RasterSourceMetadataStatus.Missing, result.Status);
        Assert.Equal("artifact_missing", result.FailureCode);
    }

    [Fact]
    public async Task ResolveAsync_Cancellation_IsPropagatedToResolver()
    {
        var resolver = new StubMetadataResolver(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return RasterSourceMetadataResolution.Failure(RasterSourceMetadataStatus.Missing, "unreachable");
        });
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            RasterSourceMetadataAdmission.ResolveAsync(Cog(), resolver, cancellationToken: cancellation.Token));
    }

    private static PostgisRasterSourceDescriptor Postgis() => new()
    {
        Version = "catalog-version-42",
        LayerId = 7,
        RasterId = 123,
        Content = Content(),
        SecurityContext = Security(),
    };

    private static ObjectStoreCogRasterSourceDescriptor Cog() => new()
    {
        Version = "object-version-1",
        StoreReference = "imagery-prod",
        ObjectKey = "tenant-a/imagery/source.tif",
        Content = Content() with { MediaType = "image/tiff", ETag = "etag-1" },
        SecurityContext = Security(),
    };

    private static ObjectStoreZarrRasterSourceDescriptor Zarr() => new()
    {
        Version = "object-version-9",
        StoreReference = "coverage-prod",
        ObjectKey = "tenant-a/climate/model.zarr",
        ArrayPath = "temperature",
        Content = Content() with { MediaType = "application/vnd+zarr", ETag = "etag-zarr" },
        SecurityContext = Security(),
    };

    private static StagedArtifactRasterSourceDescriptor Staged() => new()
    {
        Version = "generation-3",
        ArtifactReference = "artifact-01JZZZZZZZZZZZZZZZZZZZZZZZ",
        Content = Content(),
        SecurityContext = Security(),
    };

    private static InlineRasterSourceDescriptor Inline(byte[] payload) => new()
    {
        Version = "inline-v1",
        Payload = payload,
        Content = Content() with { SizeBytes = payload.Length },
        SecurityContext = Security(),
    };

    private static RasterContentIdentity Content() => new()
    {
        SizeBytes = 4096,
        MediaType = "application/octet-stream",
        Checksum = new RasterChecksum("sha256", new string('a', 64)),
    };

    private static RasterSecurityContextReference Security() => new()
    {
        TenantId = "tenant-a",
        AuthorizationSnapshotReference = "auth-snapshot-123",
    };

    private sealed class StubMetadataResolver(
        Func<RasterSourceDescriptor, CancellationToken, Task<RasterSourceMetadataResolution>> callback)
        : IRasterSourceMetadataResolver
    {
        public Task<RasterSourceMetadataResolution> ResolveMetadataAsync(
            RasterSourceDescriptor descriptor,
            CancellationToken cancellationToken = default) => callback(descriptor, cancellationToken);
    }
}

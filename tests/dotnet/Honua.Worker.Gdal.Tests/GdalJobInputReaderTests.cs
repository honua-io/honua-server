// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Honua.Core.Features.Geoprocessing.Raster;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.TestKit.Attributes;
using Honua.Worker.Gdal.Execution;

namespace Honua.Worker.Gdal.Tests;

/// <summary>
/// Unit coverage for <see cref="GdalJobInputReader"/>'s base64 decode path,
/// pinning that the <c>MaxArtifactBytes</c> ceiling is enforced BEFORE the
/// payload is materialized so an oversized input cannot drive a large
/// decode-time allocation before rejection.
/// </summary>
public sealed class GdalJobInputReaderTests
{
    private static Dictionary<string, string> Param(string name, string value)
        => new(StringComparer.Ordinal)
        {
            [GdalWorkerParameterKeys.StepInputPrefix + name] = value,
        };

    [UnitTest]
    public void TryGetRasterInput_PinnedCog_ProjectsConditionalVsiRead()
    {
        var descriptor = new ObjectStoreCogRasterSourceDescriptor
        {
            Provider = CloudStorageProvider.AwsS3,
            StoreReference = "imagery-prod",
            ObjectKey = "tenant/dem.tif",
            DeclaredDimensions = new RasterSourceDimensions(2048, 1024, 1, 16),
            Version = "version-7",
            Content = new RasterContentIdentity
            {
                SizeBytes = 8_000_000_000,
                MediaType = "image/tiff",
                ETag = "etag-7",
            },
            SecurityContext = new RasterSecurityContextReference
            {
                TenantId = "tenant-a",
                AuthorizationSnapshotReference = "catalog-registration:91",
            },
        };
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [GdalWorkerParameterKeys.StepRasterSourcePrefix + "source"] =
                RasterSourceJson.Serialize(descriptor),
        };

        var ok = GdalJobInputReader.TryGetRasterInput(
            parameters,
            "source",
            maxInlineBytes: 64 * 1024,
            out var input,
            out var error);

        ok.Should().BeTrue(error);
        input.InlineBytes.Should().BeNull();
        input.ReferencedPath.Should().Be("/vsis3/imagery-prod/tenant/dem.tif");
        input.TryAdmit(new GdalWorkerOptions(), out error).Should().BeTrue(error);
        var arguments = new List<string>();
        input.AddReadPin(arguments);
        arguments.Should().Equal("--config", "GDAL_HTTP_HEADERS", "If-Match: etag-7");
    }

    [UnitTest]
    public void TryGetRasterInput_InlineSelection_FailsClosedUntilWorkerAppliesIt()
    {
        byte[] payload = [0x49, 0x49, 0x2A, 0x00];
        var descriptor = new InlineRasterSourceDescriptor
        {
            Version = "inline-v1",
            Payload = payload,
            Content = new RasterContentIdentity
            {
                SizeBytes = payload.Length,
                MediaType = "image/tiff",
                Checksum = new RasterChecksum(
                    "sha256",
                    Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant()),
            },
            SecurityContext = new RasterSecurityContextReference
            {
                TenantId = "tenant-a",
                AuthorizationSnapshotReference = "catalog-registration:91",
            },
            Selection = new RasterSourceSelection
            {
                Bands = [1],
            },
        };
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [GdalWorkerParameterKeys.StepRasterSourcePrefix + "source"] =
                RasterSourceJson.Serialize(descriptor),
        };

        var ok = GdalJobInputReader.TryGetRasterInput(
            parameters,
            "source",
            maxInlineBytes: 64 * 1024,
            out _,
            out var error);

        ok.Should().BeFalse();
        error.Should().Contain("selection");
        error.Should().Contain("not supported");
    }

    [UnitTest]
    public void TryGetRasterInput_PinnedCogSelection_FailsClosedUntilWorkerAppliesIt()
    {
        var descriptor = new ObjectStoreCogRasterSourceDescriptor
        {
            Provider = CloudStorageProvider.AwsS3,
            StoreReference = "imagery-prod",
            ObjectKey = "tenant/dem.tif",
            DeclaredDimensions = new RasterSourceDimensions(2048, 1024, 1, 16),
            Version = "version-7",
            Content = new RasterContentIdentity
            {
                SizeBytes = 8_000_000_000,
                MediaType = "image/tiff",
                ETag = "etag-7",
            },
            SecurityContext = new RasterSecurityContextReference
            {
                TenantId = "tenant-a",
                AuthorizationSnapshotReference = "catalog-registration:91",
            },
            Selection = new RasterSourceSelection
            {
                PixelWindow = new RasterPixelWindow(0, 0, 256, 256),
            },
        };
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [GdalWorkerParameterKeys.StepRasterSourcePrefix + "source"] =
                RasterSourceJson.Serialize(descriptor),
        };

        var ok = GdalJobInputReader.TryGetRasterInput(
            parameters,
            "source",
            maxInlineBytes: 64 * 1024,
            out _,
            out var error);

        ok.Should().BeFalse();
        error.Should().Contain("selection");
        error.Should().Contain("not supported");
    }

    [UnitTest]
    public void TryGetRasterInput_PinnedCogWhenTiffIsNotAllowed_FailsAdmissionBeforeGdal()
    {
        var descriptor = new ObjectStoreCogRasterSourceDescriptor
        {
            Provider = CloudStorageProvider.AwsS3,
            StoreReference = "imagery-prod",
            ObjectKey = "tenant/dem.tif",
            DeclaredDimensions = new RasterSourceDimensions(2048, 1024, 1, 16),
            Version = "version-7",
            Content = new RasterContentIdentity
            {
                SizeBytes = 8_000_000_000,
                MediaType = "image/tiff",
                ETag = "etag-7",
            },
            SecurityContext = new RasterSecurityContextReference
            {
                TenantId = "tenant-a",
                AuthorizationSnapshotReference = "catalog-registration:91",
            },
        };
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [GdalWorkerParameterKeys.StepRasterSourcePrefix + "source"] =
                RasterSourceJson.Serialize(descriptor),
        };
        var options = new GdalWorkerOptions
        {
            AllowedRasterInputFormats = ["PNG"],
        };

        var parsed = GdalJobInputReader.TryGetRasterInput(
            parameters,
            "source",
            maxInlineBytes: 64 * 1024,
            out var input,
            out var parseError);
        var admitted = input.TryAdmit(options, out var admissionError);

        parsed.Should().BeTrue(parseError);
        admitted.Should().BeFalse();
        admissionError.Should().Contain("TIFF");
        admissionError.Should().Contain("AllowedRasterInputFormats");
    }

    [UnitTest]
    public void TryGetRasterInput_CogWithoutETag_FailsClosed()
    {
        var descriptor = new ObjectStoreCogRasterSourceDescriptor
        {
            Provider = CloudStorageProvider.AwsS3,
            StoreReference = "imagery-prod",
            ObjectKey = "tenant/dem.tif",
            Version = "version-7",
            Content = new RasterContentIdentity
            {
                SizeBytes = 4096,
                MediaType = "image/tiff",
            },
            SecurityContext = new RasterSecurityContextReference
            {
                TenantId = "tenant-a",
                AuthorizationSnapshotReference = "catalog-registration:91",
            },
        };
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [GdalWorkerParameterKeys.StepRasterSourcePrefix + "source"] =
                RasterSourceJson.Serialize(descriptor),
        };

        var ok = GdalJobInputReader.TryGetRasterInput(
            parameters,
            "source",
            maxInlineBytes: 64 * 1024,
            out _,
            out var error);

        ok.Should().BeFalse();
        error.Should().Contain("requires an ETag");
    }

    [UnitTest]
    public void TryGetRasterInput_CogWithoutBoundedDimensions_FailsClosed()
    {
        var descriptor = new ObjectStoreCogRasterSourceDescriptor
        {
            Provider = CloudStorageProvider.AwsS3,
            StoreReference = "imagery-prod",
            ObjectKey = "tenant/dem.tif",
            Version = "version-7",
            Content = new RasterContentIdentity
            {
                SizeBytes = 4096,
                MediaType = "image/tiff",
                ETag = "etag-7",
            },
            SecurityContext = new RasterSecurityContextReference
            {
                TenantId = "tenant-a",
                AuthorizationSnapshotReference = "catalog-registration:91",
            },
        };
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [GdalWorkerParameterKeys.StepRasterSourcePrefix + "source"] =
                RasterSourceJson.Serialize(descriptor),
        };

        var ok = GdalJobInputReader.TryGetRasterInput(
            parameters,
            "source",
            maxInlineBytes: 64 * 1024,
            out _,
            out var error);

        ok.Should().BeFalse();
        error.Should().Contain("requires bounded catalog dimensions");
    }

    [UnitTest]
    public void TryGetRasterInput_PinnedCogWithOversizedDimensions_FailsAdmissionBeforeGdal()
    {
        var descriptor = new ObjectStoreCogRasterSourceDescriptor
        {
            Provider = CloudStorageProvider.AwsS3,
            StoreReference = "imagery-prod",
            ObjectKey = "tenant/dem.tif",
            DeclaredDimensions = new RasterSourceDimensions(200_000, 1, 1, 8),
            Version = "version-7",
            Content = new RasterContentIdentity
            {
                SizeBytes = 4096,
                MediaType = "image/tiff",
                ETag = "etag-7",
            },
            SecurityContext = new RasterSecurityContextReference
            {
                TenantId = "tenant-a",
                AuthorizationSnapshotReference = "catalog-registration:91",
            },
        };
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [GdalWorkerParameterKeys.StepRasterSourcePrefix + "source"] =
                RasterSourceJson.Serialize(descriptor),
        };

        var parsed = GdalJobInputReader.TryGetRasterInput(
            parameters,
            "source",
            maxInlineBytes: 64 * 1024,
            out var input,
            out var parseError);
        var admitted = input.TryAdmit(new GdalWorkerOptions(), out var admissionError);

        parsed.Should().BeTrue(parseError);
        admitted.Should().BeFalse();
        admissionError.Should().Contain("MaxRasterWidth");
    }

    [UnitTest]
    public void TryGetBase64Input_OversizedPayload_RejectedByPreDecodeUpperBound()
    {
        // Encoded length whose conservative decoded upper bound ((len / 4) * 3)
        // exceeds the tiny cap, proving the PRE-DECODE guard fires on the
        // encoded length before Convert.FromBase64String is ever called: a
        // 48-byte payload encodes to 64 base64 chars, whose upper bound
        // (64 / 4) * 3 == 48 bytes exceeds the 16-byte cap and is rejected.
        const long maxBytes = 16;
        var raw = Convert.ToBase64String(Encoding.UTF8.GetBytes(new string('x', 48)));

        var ok = GdalJobInputReader.TryGetBase64Input(
            Param("source", raw),
            "source",
            maxBytes,
            out var bytes,
            out var error);

        ok.Should().BeFalse();
        bytes.Should().BeEmpty();
        error.Should().Contain("MaxArtifactBytes=16");
        error.Should().Contain("source");
    }

    [UnitTest]
    public void TryGetBase64Input_InBoundsPayload_DecodesSuccessfully()
    {
        var payload = Encoding.UTF8.GetBytes("a-modest-in-bounds-raster-payload");
        var raw = Convert.ToBase64String(payload);

        var ok = GdalJobInputReader.TryGetBase64Input(
            Param("source", raw),
            "source",
            maxBytes: 1024,
            out var bytes,
            out var error);

        ok.Should().BeTrue();
        error.Should().BeEmpty();
        bytes.Should().Equal(payload);
    }

    [UnitTest]
    public void TryGetBase64Input_ExactlyAtCap_DecodesSuccessfully()
    {
        // 24 bytes encodes to 32 base64 chars with no padding; upper bound
        // (32 / 4) * 3 == 24 == cap, so it must pass both the pre-check and
        // the exact post-decode check.
        var payload = Encoding.UTF8.GetBytes(new string('z', 24));
        var raw = Convert.ToBase64String(payload);

        var ok = GdalJobInputReader.TryGetBase64Input(
            Param("source", raw),
            "source",
            maxBytes: 24,
            out var bytes,
            out var error);

        ok.Should().BeTrue();
        error.Should().BeEmpty();
        bytes.Should().HaveCount(24);
    }

    [UnitTest]
    public void TryGetBase64Input_MissingInput_ReportsMissing()
    {
        var ok = GdalJobInputReader.TryGetBase64Input(
            new Dictionary<string, string>(StringComparer.Ordinal),
            "source",
            maxBytes: 1024,
            out var bytes,
            out var error);

        ok.Should().BeFalse();
        bytes.Should().BeEmpty();
        error.Should().Contain("missing required input 'source'");
    }

    [UnitTest]
    public void TryGetBase64Input_InvalidBase64_ReportsFormatError()
    {
        var ok = GdalJobInputReader.TryGetBase64Input(
            Param("source", "not valid base64!!!"),
            "source",
            maxBytes: 1024,
            out var bytes,
            out var error);

        ok.Should().BeFalse();
        bytes.Should().BeEmpty();
        error.Should().Contain("not valid base64");
    }

    [UnitTest]
    public void TryGetBase64Input_EmptyDecode_ReportsZeroBytes()
    {
        var ok = GdalJobInputReader.TryGetBase64Input(
            Param("source", ""),
            "source",
            maxBytes: 1024,
            out var bytes,
            out var error);

        ok.Should().BeFalse();
        bytes.Should().BeEmpty();
        error.Should().Contain("zero bytes");
    }
}

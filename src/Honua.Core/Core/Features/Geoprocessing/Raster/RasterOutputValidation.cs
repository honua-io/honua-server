// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Cryptography;

namespace Honua.Core.Features.Geoprocessing.Raster;

public sealed record RasterOutputValidationOptions
{
    public static RasterOutputValidationOptions Default { get; } = new();

    public int MaxInlineBytes { get; init; } = RasterOutputContract.MaximumInlinePayloadBytes;
}

public static class RasterOutputValidationCodes
{
    public const string UnsupportedContractVersion = "RASTER_OUTPUT_UNSUPPORTED_CONTRACT_VERSION";

    public const string InvalidField = "RASTER_OUTPUT_INVALID_FIELD";

    public const string InvalidContentIdentity = "RASTER_OUTPUT_INVALID_CONTENT_IDENTITY";

    public const string UnsafeLocator = "RASTER_OUTPUT_UNSAFE_LOCATOR";

    public const string InlinePayloadTooLarge = "RASTER_OUTPUT_INLINE_PAYLOAD_TOO_LARGE";

    public const string ChecksumMismatch = "RASTER_OUTPUT_CHECKSUM_MISMATCH";

    public const string ZarrOutputUnsupported = "RASTER_OUTPUT_ZARR_UNSUPPORTED";
}

public sealed record RasterOutputValidationError(string Code, string Field, string Message);

public sealed record RasterOutputValidationResult
{
    public IReadOnlyList<RasterOutputValidationError> Errors { get; init; } =
        Array.Empty<RasterOutputValidationError>();

    public bool IsValid => Errors.Count == 0;
}

public static class RasterOutputDescriptorValidator
{
    public static RasterOutputValidationResult Validate(
        RasterOutputDescriptor descriptor,
        RasterOutputValidationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        options ??= RasterOutputValidationOptions.Default;

        var errors = new List<RasterOutputValidationError>();

        if (descriptor.OutputContractVersion < RasterOutputContract.MinimumSupportedVersion
            || descriptor.OutputContractVersion > RasterOutputContract.CurrentVersion)
        {
            Add(errors, RasterOutputValidationCodes.UnsupportedContractVersion, "outputContractVersion",
                $"Output contract version {descriptor.OutputContractVersion} is outside the supported range.");
        }

        if (!RasterDescriptorChecks.IsOpaqueReference(descriptor.JobId))
        {
            Add(errors, RasterOutputValidationCodes.InvalidField, "jobId",
                "Job identifier must be an opaque identifier.");
        }

        if (descriptor.AttemptNumber <= 0)
        {
            Add(errors, RasterOutputValidationCodes.InvalidField, "attemptNumber",
                "Attempt number must be positive.");
        }

        if (!RasterDescriptorChecks.IsOpaqueReference(descriptor.OutputName))
        {
            Add(errors, RasterOutputValidationCodes.InvalidField, "outputName",
                "Output name must be an opaque identifier.");
        }

        if (string.IsNullOrWhiteSpace(descriptor.ProducingEngine) || !RasterDescriptorChecks.IsOpaqueReference(descriptor.ProducingEngine))
        {
            Add(errors, RasterOutputValidationCodes.InvalidField, "producingEngine",
                "Producing engine must be an opaque identifier.");
        }

        ValidateContentIdentity(descriptor.Content, errors);
        ValidateGrid(descriptor.Grid, errors);

        switch (descriptor)
        {
            case StagedObjectRasterOutputDescriptor staged:
                ValidateStagedObject(staged, errors);
                break;
            case PostgisRasterOutputDescriptor postgis:
                if (postgis.LayerId <= 0 || postgis.RasterId <= 0)
                {
                    Add(errors, RasterOutputValidationCodes.InvalidField, "layerId",
                        "PostGIS output references require positive layer and raster identifiers.");
                }

                break;
            case InlineRasterOutputDescriptor inline:
                ValidateInline(inline, options, errors);
                break;
            default:
                Add(errors, RasterOutputValidationCodes.InvalidField, "outputType",
                    "Unsupported raster output descriptor type.");
                break;
        }

        return new RasterOutputValidationResult { Errors = errors };
    }

    private static void ValidateStagedObject(
        StagedObjectRasterOutputDescriptor staged,
        List<RasterOutputValidationError> errors)
    {
        if (!RasterDescriptorChecks.IsOpaqueReference(staged.StoreReference))
        {
            Add(errors, RasterOutputValidationCodes.UnsafeLocator, "storeReference",
                "Store reference must be an opaque logical identifier, never a URI or credential.");
        }

        if (!RasterDescriptorChecks.IsSafeObjectStoreKey(staged.ObjectKey))
        {
            Add(errors, RasterOutputValidationCodes.UnsafeLocator, "objectKey",
                "objectKey must be a bounded object key without traversal, URI, query, or control syntax.");
        }

        if (LooksLikeZarr(staged.Content?.MediaType, staged.ObjectKey))
        {
            Add(errors, RasterOutputValidationCodes.ZarrOutputUnsupported, "objectKey",
                "Native Zarr outputs are multi-object hierarchies and cannot be published as a "
                + "single staged object. The hierarchy-aware Zarr publication protocol is owned by #3103.");
        }
    }

    private static void ValidateInline(
        InlineRasterOutputDescriptor inline,
        RasterOutputValidationOptions options,
        List<RasterOutputValidationError> errors)
    {
        var ceiling = Math.Min(
            options.MaxInlineBytes <= 0 ? RasterOutputContract.MaximumInlinePayloadBytes : options.MaxInlineBytes,
            RasterOutputContract.MaximumInlinePayloadBytes);

        if (inline.Payload is not { Length: > 0 })
        {
            Add(errors, RasterOutputValidationCodes.InvalidField, "payload",
                "Inline output payload must not be empty.");
            return;
        }

        if (inline.Payload.Length > ceiling)
        {
            Add(errors, RasterOutputValidationCodes.InlinePayloadTooLarge, "payload",
                $"Inline output payload of {inline.Payload.Length} bytes exceeds the {ceiling}-byte ceiling; "
                + "publish the output as a staged object artifact instead.");
            return;
        }

        if (inline.Content is { } content)
        {
            if (content.SizeBytes != inline.Payload.Length)
            {
                Add(errors, RasterOutputValidationCodes.InvalidContentIdentity, "content.sizeBytes",
                    "Inline content sizeBytes must equal the payload length.");
            }

            ValidateInlineChecksum(inline.Payload, content.Checksum, errors);
        }

        if (LooksLikeZarr(inline.Content?.MediaType, objectKey: null))
        {
            Add(errors, RasterOutputValidationCodes.ZarrOutputUnsupported, "content.mediaType",
                "Native Zarr outputs cannot be published inline; the hierarchy-aware Zarr "
                + "publication protocol is owned by #3103.");
        }
    }

    private static void ValidateContentIdentity(
        RasterContentIdentity? content,
        List<RasterOutputValidationError> errors)
    {
        if (content is null)
        {
            Add(errors, RasterOutputValidationCodes.InvalidContentIdentity, "content",
                "Content identity is required.");
            return;
        }

        if (content.SizeBytes <= 0)
        {
            Add(errors, RasterOutputValidationCodes.InvalidContentIdentity, "content.sizeBytes",
                "Content sizeBytes must be positive.");
        }

        if (!RasterDescriptorChecks.IsMediaType(content.MediaType))
        {
            Add(errors, RasterOutputValidationCodes.InvalidContentIdentity, "content.mediaType",
                "Content mediaType must be a simple IANA media type without parameters.");
        }

        if (content.Checksum is not { } checksum || !RasterDescriptorChecks.IsChecksum(checksum))
        {
            Add(errors, RasterOutputValidationCodes.InvalidContentIdentity, "content.checksum",
                "Published raster outputs require a valid sha256 or sha512 checksum.");
        }

        if (content.ETag is { } etag && (!RasterDescriptorChecks.IsSafeText(etag, 256) || etag.Contains('?')))
        {
            Add(errors, RasterOutputValidationCodes.InvalidContentIdentity, "content.eTag",
                "ETag contains unsafe characters.");
        }
    }

    private static void ValidateGrid(
        RasterOutputGridSummary? grid,
        List<RasterOutputValidationError> errors)
    {
        if (grid is null)
        {
            return;
        }

        if (grid.Width <= 0 || grid.Height <= 0 || grid.BandCount <= 0 || grid.BitsPerSample <= 0)
        {
            Add(errors, RasterOutputValidationCodes.InvalidField, "grid",
                "Grid summary dimensions must be positive.");
        }

        if (grid.PixelScale is { } scale
            && (!double.IsFinite(scale.X) || !double.IsFinite(scale.Y) || scale.X <= 0 || scale.Y <= 0))
        {
            Add(errors, RasterOutputValidationCodes.InvalidField, "grid.pixelScale",
                "Grid pixel scale values must be positive finite numbers.");
        }

        if (grid.CoordinateReferenceSystem is { } crs && !RasterDescriptorChecks.IsSafeText(crs, 256))
        {
            Add(errors, RasterOutputValidationCodes.InvalidField, "grid.coordinateReferenceSystem",
                "Grid CRS identifier contains unsafe characters.");
        }
    }

    public static bool LooksLikeZarr(string? mediaType, string? objectKey)
    {
        if (mediaType is { Length: > 0 } && mediaType.Contains("zarr", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (objectKey is { Length: > 0 })
        {
            var zarrSegments = objectKey
                .Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Where(static segment =>
                    segment.EndsWith(".zarr", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(segment, "zarr.json", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(segment, ".zgroup", StringComparison.Ordinal)
                    || string.Equals(segment, ".zarray", StringComparison.Ordinal));
            if (zarrSegments.Any())
            {
                return true;
            }
        }

        return false;
    }

    private static void ValidateInlineChecksum(
        byte[] payload,
        RasterChecksum? checksum,
        List<RasterOutputValidationError> errors)
    {
        if (checksum is null || !RasterDescriptorChecks.IsChecksum(checksum))
        {
            // The missing/malformed checksum was already reported by ValidateContentIdentity.
            return;
        }

        var actual = checksum.Algorithm switch
        {
            "sha256" => SHA256.HashData(payload),
            "sha512" => SHA512.HashData(payload),
            _ => Array.Empty<byte>(),
        };
        if (!string.Equals(Convert.ToHexString(actual), checksum.Value, StringComparison.OrdinalIgnoreCase))
        {
            Add(errors, RasterOutputValidationCodes.ChecksumMismatch, "content.checksum",
                "Inline output payload does not match its declared checksum.");
        }
    }

    private static void Add(
        List<RasterOutputValidationError> errors,
        string code,
        string field,
        string message) => errors.Add(new RasterOutputValidationError(code, field, message));
}

// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Cryptography;

namespace Honua.Core.Features.Geoprocessing.Raster;

/// <summary>Bounded validation options for raster output descriptors.</summary>
public sealed record RasterOutputValidationOptions
{
    /// <summary>Shared default options.</summary>
    public static RasterOutputValidationOptions Default { get; } = new();

    /// <summary>
    /// Deployment ceiling for inline output payload bytes. Clamped to
    /// <see cref="RasterOutputContract.MaximumInlinePayloadBytes"/>.
    /// </summary>
    public int MaxInlineBytes { get; init; } = RasterOutputContract.MaximumInlinePayloadBytes;
}

/// <summary>Stable validation codes for raster output descriptors.</summary>
public static class RasterOutputValidationCodes
{
    /// <summary>The descriptor version is outside the supported range.</summary>
    public const string UnsupportedContractVersion = "RASTER_OUTPUT_UNSUPPORTED_CONTRACT_VERSION";

    /// <summary>A required field is missing or malformed.</summary>
    public const string InvalidField = "RASTER_OUTPUT_INVALID_FIELD";

    /// <summary>The content identity block is missing or malformed.</summary>
    public const string InvalidContentIdentity = "RASTER_OUTPUT_INVALID_CONTENT_IDENTITY";

    /// <summary>An object locator contains traversal, URI, or control syntax.</summary>
    public const string UnsafeLocator = "RASTER_OUTPUT_UNSAFE_LOCATOR";

    /// <summary>The inline payload exceeds the configured ceiling.</summary>
    public const string InlinePayloadTooLarge = "RASTER_OUTPUT_INLINE_PAYLOAD_TOO_LARGE";

    /// <summary>The inline payload does not match its declared checksum.</summary>
    public const string ChecksumMismatch = "RASTER_OUTPUT_CHECKSUM_MISMATCH";

    /// <summary>
    /// A native Zarr result is a multi-object hierarchy, not one publishable object.
    /// Single-object Zarr publication is rejected fail-closed until #3103 lands the
    /// hierarchy-aware protocol; Zarr is never inserted into the COG catalog.
    /// </summary>
    public const string ZarrOutputUnsupported = "RASTER_OUTPUT_ZARR_UNSUPPORTED";
}

/// <summary>A single raster output descriptor validation failure.</summary>
/// <param name="Code">Stable machine-readable code.</param>
/// <param name="Field">Descriptor field path.</param>
/// <param name="Message">Client-safe message.</param>
public sealed record RasterOutputValidationError(string Code, string Field, string Message);

/// <summary>Validation outcome for a raster output descriptor.</summary>
public sealed record RasterOutputValidationResult
{
    /// <summary>Validation failures; empty when the descriptor is valid.</summary>
    public IReadOnlyList<RasterOutputValidationError> Errors { get; init; } =
        Array.Empty<RasterOutputValidationError>();

    /// <summary>Whether the descriptor passed validation.</summary>
    public bool IsValid => Errors.Count == 0;
}

/// <summary>
/// Bounded, allocation-conscious validation for durable raster output descriptors.
/// Applied by the publishing worker before a descriptor becomes a durable artifact
/// reference and re-applied by server-side readers before resolving one.
/// </summary>
public static class RasterOutputDescriptorValidator
{
    /// <summary>Validates one descriptor against the configured bounds.</summary>
    /// <param name="descriptor">Descriptor to validate.</param>
    /// <param name="options">Bounded validation options.</param>
    /// <returns>The validation outcome.</returns>
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

        if (!IsOpaqueReference(descriptor.JobId))
        {
            Add(errors, RasterOutputValidationCodes.InvalidField, "jobId",
                "Job identifier must be an opaque identifier.");
        }

        if (descriptor.AttemptNumber <= 0)
        {
            Add(errors, RasterOutputValidationCodes.InvalidField, "attemptNumber",
                "Attempt number must be positive.");
        }

        if (!IsOpaqueReference(descriptor.OutputName))
        {
            Add(errors, RasterOutputValidationCodes.InvalidField, "outputName",
                "Output name must be an opaque identifier.");
        }

        if (string.IsNullOrWhiteSpace(descriptor.ProducingEngine) || !IsOpaqueReference(descriptor.ProducingEngine))
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
        if (!IsOpaqueReference(staged.StoreReference))
        {
            Add(errors, RasterOutputValidationCodes.UnsafeLocator, "storeReference",
                "Store reference must be an opaque logical identifier, never a URI or credential.");
        }

        if (!IsSafeObjectStoreKey(staged.ObjectKey))
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

        if (!IsMediaType(content.MediaType))
        {
            Add(errors, RasterOutputValidationCodes.InvalidContentIdentity, "content.mediaType",
                "Content mediaType must be a simple IANA media type without parameters.");
        }

        if (content.Checksum is not { } checksum || !IsChecksum(checksum))
        {
            Add(errors, RasterOutputValidationCodes.InvalidContentIdentity, "content.checksum",
                "Published raster outputs require a valid sha256 or sha512 checksum.");
        }

        if (content.ETag is { } etag && (!IsSafeText(etag, 256) || etag.Contains('?')))
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

        if (grid.CoordinateReferenceSystem is { } crs && !IsSafeText(crs, 256))
        {
            Add(errors, RasterOutputValidationCodes.InvalidField, "grid.coordinateReferenceSystem",
                "Grid CRS identifier contains unsafe characters.");
        }
    }

    /// <summary>
    /// Detects a Zarr result by media type or object-key shape so single-object Zarr
    /// publication fails closed (#3103 owns the multi-object protocol).
    /// </summary>
    /// <param name="mediaType">Candidate media type, with or without parameters.</param>
    /// <param name="objectKey">Candidate object key or file name.</param>
    /// <returns>Whether the output looks like a Zarr hierarchy member.</returns>
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

    private static bool IsOpaqueReference(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 160)
        {
            return false;
        }

        if (value.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' or ':')))
        {
            return false;
        }

        return value[0] != '.' && !value.Contains("..", StringComparison.Ordinal);
    }

    private static bool IsSafeObjectStoreKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 2048 || !IsSafeText(value, 2048))
        {
            return false;
        }

        if (value[0] is '/' or '\\'
            || value.Contains('\\')
            || value.Contains('?')
            || value.Contains("://", StringComparison.Ordinal))
        {
            return false;
        }

        string decoded;
        try
        {
            decoded = Uri.UnescapeDataString(value);
        }
        catch (UriFormatException)
        {
            return false;
        }

        if (!IsSafeText(decoded, 2048)
            || decoded[0] is '/' or '\\'
            || decoded.Contains('\\')
            || decoded.Contains('?')
            || decoded.Contains("://", StringComparison.Ordinal))
        {
            return false;
        }

        return decoded
            .Split('/', StringSplitOptions.None)
            .All(segment => segment is not "." and not "..");
    }

    private static bool IsMediaType(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 127 || value.Contains(';'))
        {
            return false;
        }

        var slash = value.IndexOf('/');
        return slash > 0
            && slash == value.LastIndexOf('/')
            && slash < value.Length - 1
            && value.All(character => char.IsAsciiLetterOrDigit(character)
                || character is '/' or '-' or '+' or '.' or '_');
    }

    private static bool IsChecksum(RasterChecksum checksum)
    {
        var length = checksum.Algorithm switch
        {
            "sha256" => 64,
            "sha512" => 128,
            _ => 0,
        };

        return length > 0
            && checksum.Value is { } value
            && value.Length == length
            && value.All(Uri.IsHexDigit);
    }

    private static void ValidateInlineChecksum(
        byte[] payload,
        RasterChecksum? checksum,
        List<RasterOutputValidationError> errors)
    {
        if (checksum is null || !IsChecksum(checksum))
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

    private static bool IsSafeText(string value, int maximumLength) =>
        value.Length is > 0 && value.Length <= maximumLength
        && value.All(character => !char.IsControl(character));

    private static void Add(
        List<RasterOutputValidationError> errors,
        string code,
        string field,
        string message) => errors.Add(new RasterOutputValidationError(code, field, message));
}

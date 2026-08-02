// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Cryptography;

namespace Honua.Core.Features.Geoprocessing.Raster;

/// <summary>Configurable admission limits for raster output metadata.</summary>
public sealed record RasterOutputValidationOptions
{
    /// <summary>Default output validation limits.</summary>
    public static RasterOutputValidationOptions Default { get; } = new();

    /// <summary>Maximum decoded bytes permitted in an intentionally inline output.</summary>
    public int MaxInlineBytes { get; init; } = RasterOutputContract.MaximumInlineBytes;

    /// <summary>Maximum source-lineage identifiers on one output.</summary>
    public int MaxSourceArtifactIds { get; init; } = 128;

    /// <summary>Maximum staged outputs accepted in one job-attempt manifest.</summary>
    public int MaxOutputsPerManifest { get; init; } = RasterOutputContract.MaximumOutputsPerManifest;
}

/// <summary>Stable validation codes for raster output admission and publication.</summary>
public static class RasterOutputValidationCodes
{
    /// <summary>The output contract version is unsupported.</summary>
    public const string UnsupportedContractVersion = "unsupported_contract_version";

    /// <summary>A required field is missing, malformed, or outside its bounded range.</summary>
    public const string InvalidField = "invalid_field";

    /// <summary>A locator contains a URL, query, traversal, VSI path, or control character.</summary>
    public const string UnsafeLocator = "unsafe_locator";

    /// <summary>Size, media type, or strong checksum metadata is invalid.</summary>
    public const string InvalidContentIdentity = "invalid_content_identity";

    /// <summary>Inline or staged bytes do not match their declared checksum.</summary>
    public const string ChecksumMismatch = "checksum_mismatch";

    /// <summary>The inline payload exceeds its configured byte ceiling.</summary>
    public const string InlinePayloadTooLarge = "inline_payload_too_large";
}

/// <summary>A caller-safe raster output validation failure.</summary>
/// <param name="Code">Stable machine-readable code.</param>
/// <param name="Field">Field associated with the error.</param>
/// <param name="Message">Caller-safe explanation.</param>
public sealed record RasterOutputValidationError(string Code, string Field, string Message);

/// <summary>Result of validating a raster output or staged output descriptor.</summary>
public sealed record RasterOutputValidationResult
{
    /// <summary>All validation errors; an empty list indicates success.</summary>
    public required IReadOnlyList<RasterOutputValidationError> Errors { get; init; }

    /// <summary>Whether the descriptor passed validation.</summary>
    public bool IsValid => Errors.Count == 0;
}

/// <summary>Validates durable raster output descriptors without resolving or reading their content.</summary>
public static class RasterOutputDescriptorValidator
{
    /// <summary>Validates a published output descriptor.</summary>
    public static RasterOutputValidationResult Validate(
        RasterOutputDescriptor descriptor,
        RasterOutputValidationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        options ??= RasterOutputValidationOptions.Default;
        var errors = new List<RasterOutputValidationError>();

        ValidateVersion(descriptor.OutputContractVersion, errors);
        ValidateIdentifier(descriptor.ArtifactId, "artifactId", 80, errors);
        ValidateLogicalReference(descriptor.OutputName, "outputName", errors);
        ValidateContent(descriptor.Content, errors);
        ValidateGrid(descriptor.Grid, errors);
        ValidateEngine(descriptor.Engine, errors);
        ValidateLineage(descriptor.Lineage, options, errors);
        ValidateRetention(descriptor.Retention, errors);

        switch (descriptor)
        {
            case ObjectStoreRasterOutputDescriptor objectOutput:
                ValidateLogicalReference(objectOutput.StoreReference, "storeReference", errors);
                ValidateObjectKey(objectOutput.ObjectKey, "objectKey", errors);
                ValidateOpaqueVersion(objectOutput.ObjectVersion, "objectVersion", errors);
                if (objectOutput.Encoding is not RasterOutputEncoding.CloudOptimizedGeoTiff
                    and not RasterOutputEncoding.Zarr)
                {
                    Add(errors, RasterOutputValidationCodes.InvalidField, "encoding",
                        "Object raster outputs must use the COG or Zarr encoding.");
                }

                break;

            case PostgisRasterOutputDescriptor postgis:
                ValidateIdentifier(postgis.RegistrationId, "registrationId", 128, errors);
                if (postgis.LayerId < 0)
                {
                    Add(errors, RasterOutputValidationCodes.InvalidField, "layerId",
                        "PostGIS layerId must be zero or greater.");
                }

                if (postgis.RasterId <= 0)
                {
                    Add(errors, RasterOutputValidationCodes.InvalidField, "rasterId",
                        "PostGIS rasterId must be positive.");
                }

                ValidateOpaqueVersion(postgis.CatalogVersion, "catalogVersion", errors);
                break;

            case InlineRasterOutputDescriptor inline:
                ValidateInline(inline, options, errors);
                break;

            default:
                Add(errors, RasterOutputValidationCodes.InvalidField, "outputType",
                    "Raster output descriptor type is not supported.");
                break;
        }

        return new RasterOutputValidationResult { Errors = errors };
    }

    /// <summary>Validates a metadata-only staged output descriptor.</summary>
    public static RasterOutputValidationResult Validate(
        StagedRasterOutputDescriptor descriptor,
        RasterOutputValidationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        options ??= RasterOutputValidationOptions.Default;
        var errors = new List<RasterOutputValidationError>();

        ValidateVersion(descriptor.OutputContractVersion, errors);
        ValidateLogicalReference(descriptor.JobId, "jobId", errors);
        if (descriptor.Attempt < 0)
        {
            Add(errors, RasterOutputValidationCodes.InvalidField, "attempt", "Attempt must not be negative.");
        }

        ValidateLogicalReference(descriptor.OutputName, "outputName", errors);
        ValidateLogicalReference(descriptor.StoreReference, "storeReference", errors);
        ValidateObjectKey(descriptor.ObjectKey, "objectKey", errors);
        if (RasterOutputWorkerContract.IsLogicalStoreReference(descriptor.JobId)
            && RasterOutputWorkerContract.IsLogicalStoreReference(descriptor.OutputName)
            && descriptor.Attempt >= 0)
        {
            var expectedKey = RasterOutputWorkerContract.BuildStagingObjectKey(
                descriptor.JobId,
                descriptor.Attempt,
                descriptor.OutputName);
            if (!string.Equals(descriptor.ObjectKey, expectedKey, StringComparison.Ordinal))
            {
                Add(errors, RasterOutputValidationCodes.InvalidField, "objectKey",
                    "Staged object key must match its owning job, attempt, and output.");
            }
        }

        ValidateContent(descriptor.Content, errors);
        if (descriptor.Encoding is not RasterOutputEncoding.CloudOptimizedGeoTiff
            and not RasterOutputEncoding.Zarr)
        {
            Add(errors, RasterOutputValidationCodes.InvalidField, "encoding",
                "Staged raster outputs must use the COG or Zarr encoding.");
        }

        ValidateGrid(descriptor.Grid, errors);
        ValidateEngine(descriptor.Engine, errors);
        ValidateLineage(descriptor.Lineage, options, errors);
        if (descriptor.Lineage is { } lineage
            && (!string.Equals(descriptor.JobId, lineage.JobId, StringComparison.Ordinal)
                || descriptor.Attempt != lineage.Attempt))
        {
            Add(errors, RasterOutputValidationCodes.InvalidField, "lineage.jobId",
                "Staged output lineage must match its owning job and attempt.");
        }

        if (descriptor.ExpiresAt <= descriptor.CreatedAt)
        {
            Add(errors, RasterOutputValidationCodes.InvalidField, "expiresAt",
                "Staged output expiry must be later than creation.");
        }

        return new RasterOutputValidationResult { Errors = errors };
    }

    /// <summary>Validates one metadata-only job-attempt publication manifest.</summary>
    public static RasterOutputValidationResult Validate(
        RasterOutputPublicationManifest manifest,
        RasterOutputValidationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        options ??= RasterOutputValidationOptions.Default;
        cancellationToken.ThrowIfCancellationRequested();
        var errors = new List<RasterOutputValidationError>();

        ValidateVersion(manifest.OutputContractVersion, errors);
        ValidateIdentifier(manifest.JobId, "jobId", 128, errors);
        if (manifest.Attempt < 0)
        {
            Add(errors, RasterOutputValidationCodes.InvalidField, "attempt",
                "Attempt must not be negative.");
        }

        if (manifest.CreatedAt == default)
        {
            Add(errors, RasterOutputValidationCodes.InvalidField, "createdAt",
                "Publication manifest creation time is required.");
        }

        var outputLimit = Math.Min(
            Math.Max(0, options.MaxOutputsPerManifest),
            RasterOutputContract.MaximumOutputsPerManifest);
        if (manifest.Outputs is null || manifest.Outputs.Count == 0
            || manifest.Outputs.Count > outputLimit)
        {
            Add(errors, RasterOutputValidationCodes.InvalidField, "outputs",
                "Publication manifest outputs must be non-empty and within the configured count ceiling.");
            return new RasterOutputValidationResult { Errors = errors };
        }

        var outputNames = new HashSet<string>(StringComparer.Ordinal);
        var objectKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var output in manifest.Outputs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (output is null)
            {
                Add(errors, RasterOutputValidationCodes.InvalidField, "outputs",
                    "Publication manifest output entries are required.");
                continue;
            }

            errors.AddRange(Validate(output, options).Errors);
            if (!string.Equals(output.JobId, manifest.JobId, StringComparison.Ordinal)
                || output.Attempt != manifest.Attempt)
            {
                Add(errors, RasterOutputValidationCodes.InvalidField, "outputs",
                    "Every manifest output must belong to the manifest job and attempt.");
            }

            if (output.CreatedAt > manifest.CreatedAt || output.ExpiresAt <= manifest.CreatedAt)
            {
                Add(errors, RasterOutputValidationCodes.InvalidField, "outputs",
                    "Manifest creation must follow staging and precede staged-output expiry.");
            }

            if (!outputNames.Add(output.OutputName) || !objectKeys.Add(output.ObjectKey))
            {
                Add(errors, RasterOutputValidationCodes.InvalidField, "outputs",
                    "Manifest output names and staged object keys must be unique.");
            }
        }

        return new RasterOutputValidationResult { Errors = errors };
    }

    /// <summary>Checks whether a relative object key is safe for provider resolution.</summary>
    public static bool IsSafeObjectKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 2048 || value.StartsWith('/')
            || value.StartsWith('\\') || value.Contains('\\') || value.Contains('?') || value.Contains('#')
            || value.Contains(':') || value.Contains('%')
            || value.Contains("//", StringComparison.Ordinal)
            || value.Any(char.IsControl))
        {
            return false;
        }

        var segments = value.Split('/', StringSplitOptions.None);
        return segments.All(segment => segment.Length is > 0 and <= 255
            && segment is not "." and not ".."
            && segment.All(character => char.IsAsciiLetterOrDigit(character)
                || character is '-' or '_' or '.'))
            && !value.StartsWith("vsi", StringComparison.OrdinalIgnoreCase)
            && !value.StartsWith("file", StringComparison.OrdinalIgnoreCase)
            && !value.StartsWith("http", StringComparison.OrdinalIgnoreCase);
    }

    private static void ValidateVersion(int version, List<RasterOutputValidationError> errors)
    {
        if (version is < RasterOutputContract.MinimumSupportedVersion or > RasterOutputContract.CurrentVersion)
        {
            Add(errors, RasterOutputValidationCodes.UnsupportedContractVersion, "outputContractVersion",
                $"Raster output contract version {version} is not supported.");
        }
    }

    private static void ValidateContent(
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
                "Content size must be positive.");
        }

        if (!IsMediaType(content.MediaType))
        {
            Add(errors, RasterOutputValidationCodes.InvalidContentIdentity, "content.mediaType",
                "Media type must be a bounded IANA media type without transport parameters.");
        }

        if (!IsStrongChecksum(content.Checksum))
        {
            Add(errors, RasterOutputValidationCodes.InvalidContentIdentity, "content.checksum",
                "A valid sha256 or sha512 content checksum is required.");
        }

        if (content.ETag is { } etag && !IsSafeOpaqueText(etag, 256))
        {
            Add(errors, RasterOutputValidationCodes.InvalidContentIdentity, "content.eTag",
                "ETag contains URL, query, or control syntax.");
        }
    }

    private static void ValidateGrid(RasterGridMetadata? grid, List<RasterOutputValidationError> errors)
    {
        if (grid is null)
        {
            Add(errors, RasterOutputValidationCodes.InvalidField, "grid", "Grid metadata is required.");
            return;
        }

        if (string.IsNullOrWhiteSpace(grid.Crs) || grid.Crs.Length > 4096 || grid.Crs.Any(char.IsControl)
            || grid.Crs.Contains('?') || grid.Crs.Contains('#'))
        {
            Add(errors, RasterOutputValidationCodes.InvalidField, "grid.crs", "CRS metadata is invalid.");
        }

        if (grid.Width <= 0 || grid.Height <= 0 || grid.BandCount <= 0)
        {
            Add(errors, RasterOutputValidationCodes.InvalidField, "grid.dimensions",
                "Grid width, height, and band count must be positive.");
        }

        if (grid.GeoTransform is null || grid.GeoTransform.Count != 6
            || grid.GeoTransform.Any(value => double.IsNaN(value) || double.IsInfinity(value)))
        {
            Add(errors, RasterOutputValidationCodes.InvalidField, "grid.geoTransform",
                "Grid geotransform must contain six finite coefficients.");
        }
    }

    private static void ValidateEngine(RasterProducingEngine? engine, List<RasterOutputValidationError> errors)
    {
        if (engine is null || !IsIdentifier(engine.Name, 64) || !IsSafeOpaqueText(engine.Version, 128))
        {
            Add(errors, RasterOutputValidationCodes.InvalidField, "engine",
                "Producing engine name and version are required and bounded.");
        }
    }

    private static void ValidateLineage(
        RasterOutputLineage? lineage,
        RasterOutputValidationOptions options,
        List<RasterOutputValidationError> errors)
    {
        if (lineage is null)
        {
            Add(errors, RasterOutputValidationCodes.InvalidField, "lineage", "Output lineage is required.");
            return;
        }

        ValidateIdentifier(lineage.JobId, "lineage.jobId", 128, errors);
        if (lineage.Attempt < 0)
        {
            Add(errors, RasterOutputValidationCodes.InvalidField, "lineage.attempt",
                "Lineage attempt must not be negative.");
        }

        ValidateIdentifier(lineage.ProcessId, "lineage.processId", 128, errors);
        if (lineage.SourceArtifactIds is null || lineage.SourceArtifactIds.Count > options.MaxSourceArtifactIds
            || lineage.SourceArtifactIds.Any(identifier => !IsIdentifier(identifier, 128)))
        {
            Add(errors, RasterOutputValidationCodes.InvalidField, "lineage.sourceArtifactIds",
                "Source artifact identifiers must be bounded stable identities.");
        }
    }

    private static void ValidateRetention(
        RasterOutputRetention? retention,
        List<RasterOutputValidationError> errors)
    {
        if (retention is null || retention.ExpiresAt <= retention.PublishedAt)
        {
            Add(errors, RasterOutputValidationCodes.InvalidField, "retention",
                "Output expiry must be later than publication.");
        }
    }

    private static void ValidateInline(
        InlineRasterOutputDescriptor inline,
        RasterOutputValidationOptions options,
        List<RasterOutputValidationError> errors)
    {
        if (inline.Payload is null || inline.Payload.Length == 0)
        {
            Add(errors, RasterOutputValidationCodes.InvalidField, "payload", "Inline payload is required.");
            return;
        }

        var inlineLimit = Math.Min(
            Math.Max(0, options.MaxInlineBytes),
            RasterOutputContract.MaximumInlineBytes);
        if (inline.Payload.Length > inlineLimit)
        {
            Add(errors, RasterOutputValidationCodes.InlinePayloadTooLarge, "payload",
                $"Inline raster output exceeds the effective {inlineLimit}-byte ceiling.");
        }

        if (inline.Content is { } content && content.SizeBytes != inline.Payload.LongLength)
        {
            Add(errors, RasterOutputValidationCodes.InvalidContentIdentity, "content.sizeBytes",
                "Inline payload size does not match content identity.");
        }

        if (inline.Content?.Checksum is { } checksum && IsStrongChecksum(checksum))
        {
            var actual = checksum.Algorithm switch
            {
                "sha256" => SHA256.HashData(inline.Payload),
                "sha512" => SHA512.HashData(inline.Payload),
                _ => Array.Empty<byte>()
            };
            if (!CryptographicOperations.FixedTimeEquals(actual, Convert.FromHexString(checksum.Value)))
            {
                Add(errors, RasterOutputValidationCodes.ChecksumMismatch, "content.checksum",
                    "Inline raster output does not match its declared checksum.");
            }
        }
    }

    private static void ValidateObjectKey(
        string? value,
        string field,
        List<RasterOutputValidationError> errors)
    {
        if (!IsSafeObjectKey(value))
        {
            Add(errors, RasterOutputValidationCodes.UnsafeLocator, field,
                "Object key must be a relative, traversal-free key without URL or query syntax.");
        }
    }

    private static void ValidateIdentifier(
        string? value,
        string field,
        int maximumLength,
        List<RasterOutputValidationError> errors)
    {
        if (!IsIdentifier(value, maximumLength))
        {
            Add(errors, RasterOutputValidationCodes.InvalidField, field,
                "Identifier contains unsupported characters or exceeds its length limit.");
        }
    }

    private static void ValidateLogicalReference(
        string? value,
        string field,
        List<RasterOutputValidationError> errors)
    {
        if (!RasterOutputWorkerContract.IsLogicalStoreReference(value))
        {
            Add(errors, RasterOutputValidationCodes.UnsafeLocator, field,
                "Reference must be a bounded logical identifier without URI, path, query, or credential syntax.");
        }
    }

    private static void ValidateOpaqueVersion(
        string? value,
        string field,
        List<RasterOutputValidationError> errors)
    {
        if (!IsSafeOpaqueText(value, 256))
        {
            Add(errors, RasterOutputValidationCodes.UnsafeLocator, field,
                "Version must not contain URL, query, fragment, or control syntax.");
        }
    }

    private static bool IsIdentifier(string? value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maximumLength
        && value.All(character => char.IsAsciiLetterOrDigit(character)
            || character is '-' or '_' or '.' or ':');

    private static bool IsMediaType(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 127 || value.Contains(';'))
        {
            return false;
        }

        var slash = value.IndexOf('/');
        return slash > 0 && slash == value.LastIndexOf('/') && slash < value.Length - 1
            && value.All(character => char.IsAsciiLetterOrDigit(character)
                || character is '/' or '-' or '+' or '.' or '_');
    }

    private static bool IsStrongChecksum(RasterChecksum? checksum)
    {
        var length = checksum?.Algorithm switch
        {
            "sha256" => 64,
            "sha512" => 128,
            _ => 0
        };
        return length > 0 && checksum!.Value is { } value && value.Length == length && value.All(Uri.IsHexDigit);
    }

    private static bool IsSafeOpaqueText(string? value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maximumLength && !value.Any(char.IsControl)
        && !value.Contains("://", StringComparison.Ordinal) && !value.Contains('?') && !value.Contains('#');

    private static void Add(
        List<RasterOutputValidationError> errors,
        string code,
        string field,
        string message) => errors.Add(new RasterOutputValidationError(code, field, message));
}

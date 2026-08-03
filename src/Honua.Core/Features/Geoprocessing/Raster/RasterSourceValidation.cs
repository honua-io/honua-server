// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Text;
using Honua.Core.Features.Geoprocessing.Domain;

namespace Honua.Core.Features.Geoprocessing.Raster;

/// <summary>Configurable admission limits for raster source descriptors.</summary>
public sealed record RasterSourceValidationOptions
{
    /// <summary>Default validation limits.</summary>
    public static RasterSourceValidationOptions Default { get; } = new();

    /// <summary>Maximum decoded bytes accepted in an inline descriptor.</summary>
    public int MaxInlineBytes { get; init; } = 64 * 1024;

    /// <summary>Maximum band indexes accepted in one bounded selection.</summary>
    public int MaxBandSelections { get; init; } = 256;

    /// <summary>Maximum named dimension slices accepted in one bounded selection.</summary>
    public int MaxDimensionSelections { get; init; } = 64;

    /// <summary>Maximum total typed raster bindings accepted across one plan.</summary>
    public int MaxSourcesPerPlan { get; init; } = 32;

    /// <summary>Maximum length of a raster process-parameter binding name.</summary>
    public int MaxParameterNameLength { get; init; } = 64;

    /// <summary>Maximum serialized descriptor bytes accepted across one plan.</summary>
    public int MaxSerializedBytesPerPlan { get; init; } = 256 * 1024;
}

/// <summary>Stable validation error codes for raster source admission.</summary>
public static class RasterSourceValidationCodes
{
    /// <summary>The descriptor contract version is not supported.</summary>
    public const string UnsupportedContractVersion = "unsupported_contract_version";

    /// <summary>An immutable source version, ETag, or checksum is required.</summary>
    public const string ImmutableIdentityRequired = "immutable_identity_required";

    /// <summary>The source locator contains a URI, traversal, VSI path, or unsafe characters.</summary>
    public const string UnsafeLocator = "unsafe_locator";

    /// <summary>The security context is missing or resembles a path, URI, or credential value.</summary>
    public const string InvalidSecurityContext = "invalid_security_context";

    /// <summary>The inline payload exceeds the configured ceiling.</summary>
    public const string InlinePayloadTooLarge = "inline_payload_too_large";

    /// <summary>A descriptor field is missing, malformed, or outside its bounded range.</summary>
    public const string InvalidField = "invalid_field";

    /// <summary>The content size, media type, checksum, or ETag is malformed.</summary>
    public const string InvalidContentIdentity = "invalid_content_identity";

    /// <summary>The inline payload does not match its declared strong checksum.</summary>
    public const string ChecksumMismatch = "checksum_mismatch";

    /// <summary>The plan contains more typed raster bindings than the configured ceiling.</summary>
    public const string TooManySources = "too_many_sources";

    /// <summary>A raster binding key is too long or is not a safe process parameter name.</summary>
    public const string InvalidParameterName = "invalid_parameter_name";

    /// <summary>Serialized raster descriptors exceed the configured per-plan budget.</summary>
    public const string SerializedBudgetExceeded = "serialized_budget_exceeded";

    /// <summary>A typed raster source is bound to a parameter not declared as a raster input.</summary>
    public const string InvalidParameterBinding = "invalid_parameter_binding";

    /// <summary>Inline raster bytes are not permitted in durable analysis content.</summary>
    public const string InlinePersistenceDenied = "inline_persistence_denied";
}

/// <summary>A single raster source validation failure.</summary>
/// <param name="Code">Stable machine-readable error code.</param>
/// <param name="Field">Descriptor field associated with the failure.</param>
/// <param name="Message">Caller-safe validation explanation.</param>
public sealed record RasterSourceValidationError(string Code, string Field, string Message);

/// <summary>Result of validating one raster source descriptor.</summary>
public sealed record RasterSourceValidationResult
{
    /// <summary>Validation errors; an empty list indicates success.</summary>
    public required IReadOnlyList<RasterSourceValidationError> Errors { get; init; }

    /// <summary>Whether the descriptor is safe to persist.</summary>
    public bool IsValid => Errors.Count == 0;
}

/// <summary>
/// Validates versioned raster descriptors before they enter a durable job specification.
/// </summary>
public static class RasterSourceDescriptorValidator
{
    /// <summary>Validates a descriptor against the supplied admission limits.</summary>
    /// <param name="descriptor">Descriptor to validate.</param>
    /// <param name="options">Optional validation limits.</param>
    /// <param name="cancellationToken">Cancellation token checked throughout bounded collections.</param>
    /// <returns>All caller-safe validation failures.</returns>
    public static RasterSourceValidationResult Validate(
        RasterSourceDescriptor descriptor,
        RasterSourceValidationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        options ??= RasterSourceValidationOptions.Default;
        cancellationToken.ThrowIfCancellationRequested();

        var errors = new List<RasterSourceValidationError>();
        if (descriptor.SourceContractVersion is < RasterSourceContract.MinimumSupportedVersion
            or > RasterSourceContract.CurrentVersion)
        {
            Add(errors, RasterSourceValidationCodes.UnsupportedContractVersion, "sourceContractVersion",
                $"Raster source contract version {descriptor.SourceContractVersion} is not supported.");
        }

        var content = descriptor.Content;
        ValidateContentIdentity(content, errors);
        ValidateSecurityContext(descriptor.SecurityContext, errors);
        ValidateSelection(descriptor.Selection, options, errors, cancellationToken);

        if (!string.IsNullOrWhiteSpace(descriptor.Version)
            && (!IsSafeText(descriptor.Version, 512)
                || descriptor.Version.Contains("://", StringComparison.Ordinal)
                || descriptor.Version.Contains('?')
                || descriptor.Version.Contains('#')))
        {
            Add(errors, RasterSourceValidationCodes.UnsafeLocator, "version",
                "Source version contains URI, query, or unsafe control syntax.");
        }

        var hasImmutablePin = !string.IsNullOrWhiteSpace(descriptor.Version)
            || !string.IsNullOrWhiteSpace(content?.ETag)
            || content?.Checksum is not null;
        if (!hasImmutablePin)
        {
            Add(errors, RasterSourceValidationCodes.ImmutableIdentityRequired, "version",
                "A source version, ETag, or checksum is required.");
        }

        switch (descriptor)
        {
            case PostgisRasterSourceDescriptor postgis:
                if (postgis.LayerId < 0)
                {
                    Add(errors, RasterSourceValidationCodes.InvalidField, "layerId",
                        "PostGIS layerId must be zero or greater.");
                }

                if (postgis.RasterId <= 0)
                {
                    Add(errors, RasterSourceValidationCodes.InvalidField, "rasterId",
                        "PostGIS rasterId must be positive.");
                }

                if (string.IsNullOrWhiteSpace(postgis.Version))
                {
                    Add(errors, RasterSourceValidationCodes.ImmutableIdentityRequired, "version",
                        "PostGIS sources require a pinned catalog version.");
                }

                break;

            case ObjectStoreCogRasterSourceDescriptor cog:
                ValidateOpaqueReference(cog.StoreReference, "storeReference", errors);
                ValidateRelativeLocator(cog.ObjectKey, "objectKey", errors);
                if (cog.DeclaredDimensions is { } dimensions
                    && (dimensions.Width <= 0
                        || dimensions.Height <= 0
                        || dimensions.BandCount <= 0
                        || dimensions.BitsPerSample <= 0))
                {
                    Add(errors, RasterSourceValidationCodes.InvalidField, "declaredDimensions",
                        "Declared raster dimensions must all be positive when supplied.");
                }

                break;

            case ObjectStoreZarrRasterSourceDescriptor zarr:
                ValidateOpaqueReference(zarr.StoreReference, "storeReference", errors);
                ValidateRelativeLocator(zarr.ObjectKey, "objectKey", errors);
                ValidateRelativeLocator(zarr.ArrayPath, "arrayPath", errors);
                break;

            case StagedArtifactRasterSourceDescriptor staged:
                ValidateOpaqueReference(staged.ArtifactReference, "artifactReference", errors);
                break;

            case InlineRasterSourceDescriptor inline:
                if (inline.Payload is null || inline.Payload.Length == 0)
                {
                    Add(errors, RasterSourceValidationCodes.InvalidField, "payload",
                        "Inline raster payload must not be empty.");
                }
                else
                {
                    if (inline.Payload.Length > options.MaxInlineBytes)
                    {
                        Add(errors, RasterSourceValidationCodes.InlinePayloadTooLarge, "payload",
                            $"Inline raster payload exceeds the configured {options.MaxInlineBytes}-byte ceiling.");
                    }

                    if (content is not null && content.SizeBytes != inline.Payload.LongLength)
                    {
                        Add(errors, RasterSourceValidationCodes.InvalidContentIdentity, "content.sizeBytes",
                            "Inline payload length does not match content sizeBytes.");
                    }

                    ValidateInlineChecksum(inline.Payload, content?.Checksum, errors);
                }

                break;

            default:
                Add(errors, RasterSourceValidationCodes.InvalidField, "sourceType",
                    "Raster source descriptor type is not supported by this contract version.");
                break;
        }

        return new RasterSourceValidationResult { Errors = errors };
    }

    private static void ValidateContentIdentity(
        RasterContentIdentity? content,
        List<RasterSourceValidationError> errors)
    {
        if (content is null)
        {
            Add(errors, RasterSourceValidationCodes.InvalidContentIdentity, "content",
                "Content identity is required.");
            return;
        }

        if (content.SizeBytes <= 0)
        {
            Add(errors, RasterSourceValidationCodes.InvalidContentIdentity, "content.sizeBytes",
                "Content sizeBytes must be positive.");
        }

        if (!IsMediaType(content.MediaType))
        {
            Add(errors, RasterSourceValidationCodes.InvalidContentIdentity, "content.mediaType",
                "Content mediaType must be a simple IANA media type without parameters.");
        }

        if (content.Checksum is { } checksum && !IsChecksum(checksum))
        {
            Add(errors, RasterSourceValidationCodes.InvalidContentIdentity, "content.checksum",
                "Checksum must be a sha256 or sha512 hex digest.");
        }

        if (content.ETag is { } etag && (!IsSafeText(etag, 256) || etag.Contains('?')))
        {
            Add(errors, RasterSourceValidationCodes.InvalidContentIdentity, "content.eTag",
                "ETag contains unsafe characters.");
        }
    }

    private static void ValidateSecurityContext(
        RasterSecurityContextReference securityContext,
        List<RasterSourceValidationError> errors)
    {
        if (securityContext is null
            || !IsOpaqueReference(securityContext.TenantId)
            || !IsOpaqueReference(securityContext.AuthorizationSnapshotReference))
        {
            Add(errors, RasterSourceValidationCodes.InvalidSecurityContext, "securityContext",
                "Tenant and authorization snapshot references must be opaque identifiers.");
        }
    }

    private static void ValidateSelection(
        RasterSourceSelection? selection,
        RasterSourceValidationOptions options,
        List<RasterSourceValidationError> errors,
        CancellationToken cancellationToken)
    {
        if (selection is null)
        {
            return;
        }

        if (selection.PixelWindow is { } window
            && (window.X < 0 || window.Y < 0 || window.Width <= 0 || window.Height <= 0))
        {
            Add(errors, RasterSourceValidationCodes.InvalidField, "selection.pixelWindow",
                "Pixel window coordinates must be non-negative and dimensions positive.");
        }

        var bands = selection.Bands;
        if (bands is null)
        {
            Add(errors, RasterSourceValidationCodes.InvalidField, "selection.bands",
                "Band selection must be an array when supplied.");
            bands = Array.Empty<int>();
        }

        if (bands.Count > options.MaxBandSelections)
        {
            Add(errors, RasterSourceValidationCodes.InvalidField, "selection.bands",
                "Band selection exceeds the configured count limit.");
        }

        if (bands.Count <= options.MaxBandSelections)
        {
            var seenBands = new HashSet<int>();
            foreach (var band in bands)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (band <= 0 || !seenBands.Add(band))
                {
                    Add(errors, RasterSourceValidationCodes.InvalidField, "selection.bands",
                        "Band indexes must be unique positive integers.");
                    break;
                }
            }
        }

        if (selection.Time is { } time && time.End < time.Start)
        {
            Add(errors, RasterSourceValidationCodes.InvalidField, "selection.time",
                "Time selection end must not precede start.");
        }

        var dimensions = selection.Dimensions;
        if (dimensions is null)
        {
            Add(errors, RasterSourceValidationCodes.InvalidField, "selection.dimensions",
                "Dimension selection must be an array when supplied.");
            dimensions = Array.Empty<RasterDimensionSlice>();
        }

        if (dimensions.Count > options.MaxDimensionSelections)
        {
            Add(errors, RasterSourceValidationCodes.InvalidField, "selection.dimensions",
                "Dimension selection exceeds the configured count limit.");
        }

        if (dimensions.Count <= options.MaxDimensionSelections)
        {
            var seenDimensions = new HashSet<string>(StringComparer.Ordinal);
            foreach (var dimension in dimensions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (dimension is null
                    || !IsOpaqueReference(dimension.Dimension)
                    || dimension.Start < 0
                    || dimension.Stop <= dimension.Start
                    || dimension.Step <= 0
                    || !seenDimensions.Add(dimension.Dimension))
                {
                    Add(errors, RasterSourceValidationCodes.InvalidField, "selection.dimensions",
                        "Dimension slices require unique names and a bounded positive half-open range.");
                    break;
                }
            }
        }
    }

    private static void ValidateOpaqueReference(
        string value,
        string field,
        List<RasterSourceValidationError> errors)
    {
        if (!IsOpaqueReference(value))
        {
            Add(errors, RasterSourceValidationCodes.UnsafeLocator, field,
                $"{field} must be an opaque identifier, not a URI or path.");
        }
    }

    private static void ValidateRelativeLocator(
        string value,
        string field,
        List<RasterSourceValidationError> errors)
    {
        if (!IsSafeRelativeLocator(value))
        {
            Add(errors, RasterSourceValidationCodes.UnsafeLocator, field,
                $"{field} must be a relative object key without traversal, URI, query, or VSI syntax.");
        }
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

    private static bool IsSafeRelativeLocator(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 2048 || !IsSafeText(value, 2048))
        {
            return false;
        }

        if (value[0] == '/'
            || value.StartsWith('\\')
            || value.Contains('\\')
            || value.Contains('?')
            || value.Contains('#')
            || value.Contains("://", StringComparison.Ordinal)
            || value.Contains(':'))
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
            || decoded.StartsWith('/')
            || decoded.StartsWith('\\')
            || decoded.Contains('\\')
            || decoded.Contains('?')
            || decoded.Contains('#')
            || decoded.Contains(':'))
        {
            return false;
        }

        var segments = decoded.Split('/', StringSplitOptions.None);
        if (segments.Any(segment => string.IsNullOrWhiteSpace(segment) || segment is "." or ".."))
        {
            return false;
        }

        var first = segments[0];
        return !first.StartsWith("vsi", StringComparison.OrdinalIgnoreCase)
            && !first.StartsWith("/vsi", StringComparison.OrdinalIgnoreCase)
            && !decoded.StartsWith("file", StringComparison.OrdinalIgnoreCase)
            && !decoded.StartsWith("http", StringComparison.OrdinalIgnoreCase);
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
        List<RasterSourceValidationError> errors)
    {
        if (checksum is null || !IsChecksum(checksum))
        {
            Add(errors, RasterSourceValidationCodes.InvalidContentIdentity, "content.checksum",
                "Inline raster payloads require a valid sha256 or sha512 checksum.");
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
            Add(errors, RasterSourceValidationCodes.ChecksumMismatch, "content.checksum",
                "Inline raster payload does not match its declared checksum.");
        }
    }

    private static bool IsSafeText(string value, int maximumLength) =>
        value.Length is > 0 && value.Length <= maximumLength
        && value.All(character => !char.IsControl(character));

    private static void Add(
        List<RasterSourceValidationError> errors,
        string code,
        string field,
        string message) => errors.Add(new RasterSourceValidationError(code, field, message));
}

/// <summary>Applies bounded validation to all typed raster bindings in an analysis plan.</summary>
public static class RasterSourcePlanValidator
{
    /// <summary>Validates binding keys, descriptors, count, and serialized-size budgets.</summary>
    public static RasterSourceValidationResult Validate(
        AnalysisPlan plan,
        RasterSourceValidationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        options ??= RasterSourceValidationOptions.Default;
        cancellationToken.ThrowIfCancellationRequested();

        var errors = new List<RasterSourceValidationError>();
        var sourceCount = 0;
        var serializedBytes = 0L;
        var sourceLimitExceeded = false;
        var serializedBudgetExceeded = false;
        if (plan.Steps is null)
        {
            errors.Add(new RasterSourceValidationError(
                RasterSourceValidationCodes.InvalidField,
                "steps",
                "Analysis plan steps are required."));
            return new RasterSourceValidationResult { Errors = errors };
        }

        foreach (var step in plan.Steps)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (step?.RasterSources is null)
            {
                errors.Add(new RasterSourceValidationError(
                    RasterSourceValidationCodes.InvalidField,
                    "rasterSources",
                    "Raster source bindings must be an object when supplied."));
                continue;
            }

            foreach (var source in step.RasterSources)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sourcePath = $"steps[{step.StepId}].raster_sources.{source.Key}";
                sourceCount++;
                if (sourceCount > options.MaxSourcesPerPlan)
                {
                    errors.Add(new RasterSourceValidationError(
                        RasterSourceValidationCodes.TooManySources,
                        "rasterSources",
                        $"Plan contains more than the configured maximum of {options.MaxSourcesPerPlan} raster sources."));
                    sourceLimitExceeded = true;
                    break;
                }

                if (!IsSafeParameterName(source.Key, options.MaxParameterNameLength))
                {
                    errors.Add(new RasterSourceValidationError(
                        RasterSourceValidationCodes.InvalidParameterName,
                        "rasterSources",
                        $"Raster source binding '{source.Key}' is not a bounded process parameter name."));
                }

                if (source.Value is null)
                {
                    errors.Add(new RasterSourceValidationError(
                        RasterSourceValidationCodes.InvalidField,
                        sourcePath,
                        "Raster source descriptor is required."));
                    continue;
                }

                errors.AddRange(RasterSourceDescriptorValidator.Validate(
                        source.Value,
                        options,
                        cancellationToken)
                    .Errors
                    .Select(error => error with { Field = $"{sourcePath}.{error.Field}" }));
                if (!serializedBudgetExceeded)
                {
                    try
                    {
                        serializedBytes += Encoding.UTF8.GetByteCount(RasterSourceJson.Serialize(source.Value));
                    }
                    catch (NotSupportedException)
                    {
                        errors.Add(new RasterSourceValidationError(
                            RasterSourceValidationCodes.InvalidField,
                            sourcePath,
                            "Raster source descriptor type cannot be serialized by this contract version."));
                    }

                    if (serializedBytes > options.MaxSerializedBytesPerPlan)
                    {
                        errors.Add(new RasterSourceValidationError(
                            RasterSourceValidationCodes.SerializedBudgetExceeded,
                            "rasterSources",
                            $"Serialized raster source descriptors exceed the configured {options.MaxSerializedBytesPerPlan}-byte budget."));
                        serializedBudgetExceeded = true;
                    }
                }
            }

            if (sourceLimitExceeded)
            {
                break;
            }
        }

        return new RasterSourceValidationResult { Errors = errors };
    }

    private static bool IsSafeParameterName(string? value, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > maximumLength
            || !(char.IsAsciiLetter(value[0]) || value[0] == '_'))
        {
            return false;
        }

        return value.All(character => char.IsAsciiLetterOrDigit(character)
            || character is '_' or '-' or '.');
    }
}

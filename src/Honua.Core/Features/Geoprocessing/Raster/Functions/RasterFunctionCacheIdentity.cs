// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Honua.Core.Features.Raster.Domain;

namespace Honua.Core.Features.Geoprocessing.Raster.Functions;

/// <summary>
/// Complete semantic identity for a raster-function result cache entry. The closed model
/// intentionally has no URI, object key, path, credential, token, or authorization fields.
/// </summary>
public sealed record RasterFunctionCacheIdentity
{
    /// <summary>Tenant security boundary.</summary>
    public required string TenantId { get; init; }

    /// <summary>Tenant-scoped stored function name.</summary>
    public required string FunctionName { get; init; }

    /// <summary>Exact immutable stored function version.</summary>
    public required int FunctionVersion { get; init; }

    /// <summary>Exact normalized function-definition hash.</summary>
    public required string DefinitionHash { get; init; }

    /// <summary>Version of the provider-neutral function semantics.</summary>
    public required string SemanticVersion { get; init; }

    /// <summary>Version of the selected implementation/planner.</summary>
    public required string ImplementationVersion { get; init; }

    /// <summary>Immutable, non-locator identities of all named source bindings.</summary>
    public required IReadOnlyList<RasterFunctionSourceCacheIdentity> Sources { get; init; }

    /// <summary>Exact output grid.</summary>
    public required RasterFunctionGridCacheIdentity Grid { get; init; }

    /// <summary>Optional exact temporal selection.</summary>
    public RasterFunctionTimeCacheIdentity? Time { get; init; }

    /// <summary>One-based output bands in semantically significant order.</summary>
    public IReadOnlyList<int> Bands { get; init; } = Array.Empty<int>();

    /// <summary>Exact output rendering parameters.</summary>
    public required RasterFunctionRenderCacheIdentity Render { get; init; }
}

/// <summary>Cache-safe identity of one immutable raster input.</summary>
public sealed record RasterFunctionSourceCacheIdentity
{
    /// <summary>Function input binding name.</summary>
    public required string BindingName { get; init; }

    /// <summary>Storage-neutral source class.</summary>
    public required RasterFunctionCacheSourceKind SourceKind { get; init; }

    /// <summary>
    /// Tenant-scoped logical catalog identifier. This is a safe identifier, never a URI,
    /// object key, filesystem path, connection string, or provider locator.
    /// </summary>
    public required string LogicalSourceId { get; init; }

    /// <summary>Immutable catalog/object/artifact generation.</summary>
    public required string ImmutableVersion { get; init; }

    /// <summary>Lower-case SHA-256 of source content.</summary>
    public required string ContentSha256 { get; init; }
}

/// <summary>Storage-neutral source classes allowed in cache identity.</summary>
public enum RasterFunctionCacheSourceKind
{
    /// <summary>Tenant catalog entry backed by PostGIS raster.</summary>
    Postgis = 0,

    /// <summary>Registered immutable Cloud Optimized GeoTIFF catalog entry.</summary>
    CloudOptimizedGeoTiff = 1,

    /// <summary>Registered immutable Zarr catalog entry.</summary>
    Zarr = 2,

    /// <summary>Immutable staged artifact.</summary>
    StagedArtifact = 3,

    /// <summary>Bounded inline content identified only by its digest.</summary>
    Inline = 4,
}

/// <summary>Exact affine output grid included in cache identity.</summary>
public sealed record RasterFunctionGridCacheIdentity
{
    /// <summary>Positive output SRID.</summary>
    public required int Srid { get; init; }

    /// <summary>Positive output width in pixels.</summary>
    public required int Width { get; init; }

    /// <summary>Positive output height in pixels.</summary>
    public required int Height { get; init; }

    /// <summary>X coordinate of the affine origin.</summary>
    public required double OriginX { get; init; }

    /// <summary>Y coordinate of the affine origin.</summary>
    public required double OriginY { get; init; }

    /// <summary>Non-zero affine X pixel scale.</summary>
    public required double PixelWidth { get; init; }

    /// <summary>Non-zero affine Y pixel scale.</summary>
    public required double PixelHeight { get; init; }

    /// <summary>Affine row rotation term.</summary>
    public required double RotationX { get; init; }

    /// <summary>Affine column rotation term.</summary>
    public required double RotationY { get; init; }
}

/// <summary>Exact inclusive temporal selection included in cache identity.</summary>
/// <param name="Start">Inclusive start instant.</param>
/// <param name="End">Inclusive end instant.</param>
public sealed record RasterFunctionTimeCacheIdentity(DateTimeOffset Start, DateTimeOffset End);

/// <summary>Closed render-parameter set included in cache identity.</summary>
public sealed record RasterFunctionRenderCacheIdentity
{
    /// <summary>Encoded output format.</summary>
    public required RasterFormat OutputFormat { get; init; }

    /// <summary>Encoder quality from 1 to 100.</summary>
    public required int Quality { get; init; }

    /// <summary>Whether NoData is rendered transparently.</summary>
    public required bool Transparent { get; init; }

    /// <summary>RGBA background color.</summary>
    public required uint BackgroundColor { get; init; }

    /// <summary>Optional finite output NoData value.</summary>
    public double? NoData { get; init; }

    /// <summary>Allowlisted output-grid resampling algorithm.</summary>
    public required ResamplingAlgorithm Resampling { get; init; }
}

/// <summary>AOT-safe deterministic cache-key builder for raster-function results.</summary>
public static class RasterFunctionCacheKey
{
    private const string Prefix = "raster-function:v1:";

    /// <summary>Validates, canonicalizes, and hashes a complete cache identity.</summary>
    public static string Build(RasterFunctionCacheIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        Validate(identity);
        var normalized = identity with
        {
            Sources = identity.Sources.OrderBy(static source => source.BindingName, StringComparer.Ordinal).ToArray(),
            Time = identity.Time is null
                ? null
                : new RasterFunctionTimeCacheIdentity(
                    identity.Time.Start.ToUniversalTime(),
                    identity.Time.End.ToUniversalTime()),
        };
        var json = JsonSerializer.Serialize(normalized, RasterFunctionJsonContext.Default.RasterFunctionCacheIdentity);
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        return Prefix + Convert.ToHexString(digest).ToLowerInvariant();
    }

    private static void Validate(RasterFunctionCacheIdentity identity)
    {
        RasterFunctionDefinitionStoreValidation.ValidateBoundedValue(
            identity.TenantId,
            RasterFunctionDefinitionStoreValidation.MaximumTenantIdLength,
            nameof(identity.TenantId));
        RasterFunctionDefinitionStoreValidation.ValidateBoundedValue(
            identity.FunctionName,
            RasterFunctionDefinitionStoreValidation.MaximumNameLength,
            nameof(identity.FunctionName));
        if (identity.FunctionVersion <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(identity), "Function version must be positive.");
        }

        RasterFunctionDefinitionStoreValidation.ValidateSha256(identity.DefinitionHash, nameof(identity.DefinitionHash));
        ValidateVersion(identity.SemanticVersion, nameof(identity.SemanticVersion));
        ValidateVersion(identity.ImplementationVersion, nameof(identity.ImplementationVersion));
        ArgumentNullException.ThrowIfNull(identity.Sources);
        if (identity.Sources.Count is 0 or > 32)
        {
            throw new ArgumentException("Cache identity requires 1 to 32 source bindings.", nameof(identity));
        }

        var bindings = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in identity.Sources)
        {
            ArgumentNullException.ThrowIfNull(source);
            ValidateSafeIdentifier(source.BindingName, 64, nameof(source.BindingName));
            if (!bindings.Add(source.BindingName))
            {
                throw new ArgumentException("Cache identity contains a duplicate source binding.", nameof(identity));
            }

            if (!Enum.IsDefined(source.SourceKind))
            {
                throw new ArgumentException("Cache identity contains an unsupported source kind.", nameof(identity));
            }

            ValidateSafeIdentifier(source.LogicalSourceId, 128, nameof(source.LogicalSourceId));
            ValidateVersion(source.ImmutableVersion, nameof(source.ImmutableVersion));
            RasterFunctionDefinitionStoreValidation.ValidateSha256(source.ContentSha256, nameof(source.ContentSha256));
        }

        ArgumentNullException.ThrowIfNull(identity.Grid);
        var grid = identity.Grid;
        if (grid.Srid <= 0 || grid.Width <= 0 || grid.Height <= 0
            || !AreFinite(grid.OriginX, grid.OriginY, grid.PixelWidth, grid.PixelHeight, grid.RotationX, grid.RotationY)
            || grid.PixelWidth == 0 || grid.PixelHeight == 0)
        {
            throw new ArgumentException("Cache identity output grid is invalid.", nameof(identity));
        }

        if (identity.Time is { } time && (time.Start > time.End))
        {
            throw new ArgumentException("Cache identity time range is invalid.", nameof(identity));
        }

        ArgumentNullException.ThrowIfNull(identity.Bands);
        if (identity.Bands.Any(static band => band <= 0) || identity.Bands.Distinct().Count() != identity.Bands.Count)
        {
            throw new ArgumentException("Cache identity bands must be unique positive indexes.", nameof(identity));
        }

        ArgumentNullException.ThrowIfNull(identity.Render);
        if (!Enum.IsDefined(identity.Render.OutputFormat)
            || !Enum.IsDefined(identity.Render.Resampling)
            || identity.Render.Quality is < 1 or > 100
            || identity.Render.NoData is { } noData && !double.IsFinite(noData))
        {
            throw new ArgumentException("Cache identity render parameters are invalid.", nameof(identity));
        }
    }

    private static void ValidateVersion(string value, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length is 0 or > 128 || value != value.Trim()
            || value.Any(static character => !char.IsAsciiLetterOrDigit(character) && character is not '.' and not '_' and not '-'))
        {
            throw new ArgumentException("Version must use only letters, digits, dots, underscores, and hyphens.", parameterName);
        }
    }

    private static void ValidateSafeIdentifier(string value, int maximumLength, string parameterName)
    {
        RasterFunctionDefinitionStoreValidation.ValidateBoundedValue(value, maximumLength, parameterName);
        if (value.Any(static character => !char.IsAsciiLetterOrDigit(character) && character is not '_' and not '-'))
        {
            throw new ArgumentException("Identifier contains a character outside the cache-safe allowlist.", parameterName);
        }
    }

    private static bool AreFinite(params double[] values) => values.All(double.IsFinite);
}

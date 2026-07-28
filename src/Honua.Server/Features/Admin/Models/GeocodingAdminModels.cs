// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.Admin.Models;

/// <summary>
/// Response model for geocoding provider status.
/// </summary>
public sealed class GeocodingProvidersResponse
{
    /// <summary>
    /// Name of the default geocoding provider.
    /// </summary>
    public string? DefaultProvider { get; init; }

    /// <summary>
    /// Whether provider failover is enabled.
    /// </summary>
    public required bool FailoverEnabled { get; init; }

    /// <summary>
    /// List of configured geocoding providers with health status.
    /// </summary>
    public required GeocodingProviderDetail[] Providers { get; init; }
}

/// <summary>
/// Detailed status of a single geocoding provider.
/// </summary>
public sealed class GeocodingProviderDetail
{
    /// <summary>
    /// Provider name.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Whether the provider is healthy.
    /// </summary>
    public required bool IsHealthy { get; init; }

    /// <summary>
    /// Error message if the provider is unhealthy.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// When the health was last checked.
    /// </summary>
    public DateTime? LastChecked { get; init; }

    /// <summary>
    /// Response time in milliseconds for the last health check.
    /// </summary>
    public double? ResponseTimeMs { get; init; }

    /// <summary>
    /// Provider capabilities.
    /// </summary>
    public required GeocodingProviderCapabilitiesDto Capabilities { get; init; }
}

/// <summary>
/// DTO for geocoding provider capabilities (AOT-safe, no Dictionary{string, object}).
/// </summary>
public sealed class GeocodingProviderCapabilitiesDto
{
    /// <summary>
    /// Whether forward geocoding is supported.
    /// </summary>
    public required bool SupportsForwardGeocode { get; init; }

    /// <summary>
    /// Whether reverse geocoding is supported.
    /// </summary>
    public required bool SupportsReverseGeocode { get; init; }

    /// <summary>
    /// Whether suggestion/autocomplete is supported.
    /// </summary>
    public required bool SupportsSuggest { get; init; }

    /// <summary>
    /// Whether batch geocoding is supported.
    /// </summary>
    public required bool SupportsBatch { get; init; }

    /// <summary>
    /// Maximum results per request.
    /// </summary>
    public required int MaxResultsPerRequest { get; init; }

    /// <summary>
    /// Rate limit in requests per minute, null if unlimited.
    /// </summary>
    public int? RateLimitPerMinute { get; init; }

    /// <summary>
    /// Whether the provider requires authentication.
    /// </summary>
    public required bool RequiresAuthentication { get; init; }
}

/// <summary>
/// Response for an Esri <c>.loc</c>/<c>.lox</c> locator import (#2152).
/// </summary>
public sealed class EsriLocatorImportResponse
{
    /// <summary>
    /// Imported locator name (from the uploaded file name unless overridden).
    /// </summary>
    public required string LocatorName { get; init; }

    /// <summary>
    /// Geocoding provider that serves the imported locator.
    /// </summary>
    public required string Provider { get; init; }

    /// <summary>
    /// Schema of the reference table the locator data was loaded into.
    /// </summary>
    public required string Schema { get; init; }

    /// <summary>
    /// Reference table the locator data was loaded into.
    /// </summary>
    public required string Table { get; init; }

    /// <summary>
    /// Whether reference data was supplied and loaded (parse/classify-only imports return false).
    /// </summary>
    public required bool ReferenceDataImported { get; init; }

    /// <summary>
    /// Number of reference rows loaded.
    /// </summary>
    public required int RecordsImported { get; init; }

    /// <summary>
    /// Number of reference rows skipped.
    /// </summary>
    public required int RecordsSkipped { get; init; }

    /// <summary>
    /// Details for skipped rows (capped; recordsSkipped is the full count).
    /// </summary>
    public required EsriLocatorSkippedRowDto[] SkippedRows { get; init; }

    /// <summary>
    /// Locator definition version parsed from the .loc file, when present.
    /// </summary>
    public string? Version { get; init; }

    /// <summary>
    /// Locator style class id parsed from the .loc file, when present.
    /// </summary>
    public string? StyleId { get; init; }

    /// <summary>
    /// Locator category parsed from the .loc file, when present.
    /// </summary>
    public string? Category { get; init; }

    /// <summary>
    /// Match settings recorded from the source locator (recorded, not yet applied to runtime scoring).
    /// </summary>
    public required EsriLocatorMatchSettingsDto MatchSettings { get; init; }

    /// <summary>
    /// Translation report covering every source construct; unsupported constructs are explicit.
    /// </summary>
    public required EsriLocatorReportEntryDto[] Report { get; init; }
}

/// <summary>
/// A single translation-report entry of an Esri locator import.
/// </summary>
public sealed class EsriLocatorReportEntryDto
{
    /// <summary>
    /// The source construct (a .loc property key, file name, or CSV column).
    /// </summary>
    public required string Item { get; init; }

    /// <summary>
    /// Translation status: supported, unsupported, regenerated, or ignored.
    /// </summary>
    public required string Status { get; init; }

    /// <summary>
    /// Optional explanation.
    /// </summary>
    public string? Detail { get; init; }
}

/// <summary>
/// A reference row skipped during an Esri locator import.
/// </summary>
public sealed class EsriLocatorSkippedRowDto
{
    /// <summary>
    /// 1-based data row number (excluding the header row).
    /// </summary>
    public required int RowNumber { get; init; }

    /// <summary>
    /// Why the row was skipped.
    /// </summary>
    public required string Reason { get; init; }
}

/// <summary>
/// Match settings recorded from an imported Esri locator.
/// </summary>
public sealed class EsriLocatorMatchSettingsDto
{
    /// <summary>Minimum match score of the source locator (0-100).</summary>
    public double? MinimumMatchScore { get; init; }

    /// <summary>Minimum candidate score of the source locator (0-100).</summary>
    public double? MinimumCandidateScore { get; init; }

    /// <summary>Spelling sensitivity of the source locator (0-100).</summary>
    public double? SpellingSensitivity { get; init; }

    /// <summary>Side offset of the source locator.</summary>
    public double? SideOffset { get; init; }

    /// <summary>Units of the side offset.</summary>
    public string? SideOffsetUnits { get; init; }

    /// <summary>End offset of the source locator.</summary>
    public double? EndOffset { get; init; }

    /// <summary>Whether the source locator matched on tied scores.</summary>
    public bool? MatchIfScoresTie { get; init; }

    /// <summary>Whether the source locator interpolated along address ranges.</summary>
    public bool? Interpolate { get; init; }
}

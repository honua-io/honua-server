// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Server.Features.Admin.Models;

public sealed class GeocodingProvidersResponse
{
    public string? DefaultProvider { get; init; }

    public required bool FailoverEnabled { get; init; }

    public required GeocodingProviderDetail[] Providers { get; init; }
}

public sealed class GeocodingProviderDetail
{
    public required string Name { get; init; }

    public required bool IsHealthy { get; init; }

    public string? ErrorMessage { get; init; }

    public DateTime? LastChecked { get; init; }

    public double? ResponseTimeMs { get; init; }

    public required GeocodingProviderCapabilitiesDto Capabilities { get; init; }
}

public sealed class GeocodingProviderCapabilitiesDto
{
    public required bool SupportsForwardGeocode { get; init; }

    public required bool SupportsReverseGeocode { get; init; }

    public required bool SupportsSuggest { get; init; }

    public required bool SupportsBatch { get; init; }

    public required int MaxResultsPerRequest { get; init; }

    public int? RateLimitPerMinute { get; init; }

    public required bool RequiresAuthentication { get; init; }
}

public sealed class GeocoderReferenceDataImportResponse
{
    public required string LocatorName { get; init; }

    public required string Provider { get; init; }

    public required string Schema { get; init; }

    public required string Table { get; init; }

    public required int RecordsImported { get; init; }

    public required int RecordsSkipped { get; init; }

    public required GeocoderReferenceSkippedRowDto[] SkippedRows { get; init; }

    public required GeocoderReferenceReportEntryDto[] Report { get; init; }
}

public sealed class GeocoderReferenceReportEntryDto
{
    public required string Column { get; init; }

    public required string Status { get; init; }

    public string? Detail { get; init; }
}

public sealed class GeocoderReferenceSkippedRowDto
{
    public required int RowNumber { get; init; }

    public required string Reason { get; init; }
}

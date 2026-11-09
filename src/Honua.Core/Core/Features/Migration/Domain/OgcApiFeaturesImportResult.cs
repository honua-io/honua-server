// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.FileImport.Services;
namespace Honua.Core.Features.Migration.Domain;

public sealed record OgcApiFeaturesImportResult
{
    public required bool Success { get; init; }

    public required string CollectionId { get; init; }

    public required string Target { get; init; }

    public int FeaturesImported { get; init; }

    public int FeaturesSkipped { get; init; }

    public int PagesFetched { get; init; }

    public bool Truncated { get; init; }

    public string? ErrorMessage { get; init; }

    public string? ErrorCode { get; init; }

    public IReadOnlyList<string> Warnings { get; init; } = [];

    public TimeSpan Duration { get; init; }

    // A narrower filter does not delete rows that fell out of scope; it records a manual-review warning instead.
    public bool ScopeDriftDetected { get; init; }

    public string? ManualReviewReason { get; init; }

    public IReadOnlyList<OgcApiFeaturesSchemaMappingDiagnostic> MappingDiagnostics { get; init; } = [];

    public long? SourceFeatureCountReported { get; init; }

    public OgcApiFeaturesFeatureCountParity? FeatureCountParity { get; init; }
}

public static class OgcApiFeaturesFeatureCountParityStates
{
    public const string Pass = "pass";

    public const string Fail = "fail";

    public const string NotApplicable = "not-applicable";
}

public sealed record OgcApiFeaturesFeatureCountParity
{
    public string ProbeId { get; init; } = "feature-count";

    public required string State { get; init; }

    public long? Expected { get; init; }

    public long Observed { get; init; }

    // The summary is built only from counts and known truncation reasons, and must not contain credentials.
    public required string Summary { get; init; }
}

public static class OgcApiFeaturesImportErrorCodes
{
    public const string InvalidServiceUrl = "ogc_api_features.invalid_service_url";

    public const string SourceUnreachable = "ogc_api_features.source_unreachable";

    public const string MissingItemsEndpoint = "ogc_api_features.missing_items_endpoint";

    public const string UnsupportedItemsEncoding = "ogc_api_features.unsupported_items_encoding";

    public const string InvalidItemsDocument = "ogc_api_features.invalid_items_document";

    public const string SinkFailure = "ogc_api_features.sink_failure";

    public const string Timeout = "ogc_api_features.timeout";

    public const string InvalidFilter = "ogc_api_features.invalid_filter";

    public const string InvalidBbox = "ogc_api_features.invalid_bbox";

    public const string InvalidDatetime = "ogc_api_features.invalid_datetime";
}

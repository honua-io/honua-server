// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.FileImport.Services;

namespace Honua.Core.Features.Migration.Domain;

// A clean name-and-type match is not reported; a widening conversion is informational, a narrowing conversion warns, and a missing column is an error.
public sealed record OgcApiFeaturesSchemaMappingDiagnostic
{
    public required string PropertyName { get; init; }

    public required string SourceType { get; init; }

    public string? TargetColumnType { get; init; }

    public required OgcApiFeaturesSchemaMappingClassification Classification { get; init; }

    public required string Severity { get; init; }

    public required string Reason { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter<OgcApiFeaturesSchemaMappingClassification>))]
public enum OgcApiFeaturesSchemaMappingClassification
{
    // This value is never emitted; it exists so the taxonomy stays exhaustive for a clean match.
    Automated = 0,

    Assisted = 1,

    ManualReview = 2,

    Unsupported = 3
}

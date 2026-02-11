// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.AspNetCore.Mvc;

namespace Honua.Server.Features.OData;

/// <summary>
/// Common OData query options for list and count endpoints.
/// </summary>
internal sealed record ODataQueryOptions
{
    [FromQuery(Name = "$filter")] public string? Filter { get; init; }
    [FromQuery(Name = "$select")] public string? Select { get; init; }
    [FromQuery(Name = "$orderby")] public string? Orderby { get; init; }
    [FromQuery(Name = "$top")] public string? Top { get; init; }
    [FromQuery(Name = "$skip")] public string? Skip { get; init; }
    [FromQuery(Name = "$skiptoken")] public string? Skiptoken { get; init; }
    [FromQuery(Name = "$count")] public string? Count { get; init; }
    [FromQuery(Name = "$expand")] public string? Expand { get; init; }
    [FromQuery(Name = "$compute")] public string? Compute { get; init; }
    [FromQuery(Name = "$search")] public string? Search { get; init; }
    [FromQuery(Name = "$apply")] public string? Apply { get; init; }
    [FromQuery(Name = "$format")] public string? Format { get; init; }
}

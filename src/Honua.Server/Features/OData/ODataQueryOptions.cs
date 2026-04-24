// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.AspNetCore.Mvc;

namespace Honua.Server.Features.OData;

/// <summary>
/// Common OData query options for list and count endpoints.
/// </summary>
internal sealed record ODataQueryOptions
{
    [FromQuery(Name = "$filter")] public string? DollarFilter { get; init; }
    [FromQuery(Name = "filter")] public string? BareFilter { get; init; }
    [FromQuery(Name = "$select")] public string? DollarSelect { get; init; }
    [FromQuery(Name = "select")] public string? BareSelect { get; init; }
    [FromQuery(Name = "$orderby")] public string? DollarOrderby { get; init; }
    [FromQuery(Name = "orderby")] public string? BareOrderby { get; init; }
    [FromQuery(Name = "$top")] public string? DollarTop { get; init; }
    [FromQuery(Name = "top")] public string? BareTop { get; init; }
    [FromQuery(Name = "$skip")] public string? DollarSkip { get; init; }
    [FromQuery(Name = "skip")] public string? BareSkip { get; init; }
    [FromQuery(Name = "$skiptoken")] public string? DollarSkiptoken { get; init; }
    [FromQuery(Name = "skiptoken")] public string? BareSkiptoken { get; init; }
    [FromQuery(Name = "$count")] public string? DollarCount { get; init; }
    [FromQuery(Name = "count")] public string? BareCount { get; init; }
    [FromQuery(Name = "$expand")] public string? DollarExpand { get; init; }
    [FromQuery(Name = "expand")] public string? BareExpand { get; init; }
    [FromQuery(Name = "$compute")] public string? DollarCompute { get; init; }
    [FromQuery(Name = "compute")] public string? BareCompute { get; init; }
    [FromQuery(Name = "$search")] public string? DollarSearch { get; init; }
    [FromQuery(Name = "search")] public string? BareSearch { get; init; }
    [FromQuery(Name = "$apply")] public string? DollarApply { get; init; }
    [FromQuery(Name = "apply")] public string? BareApply { get; init; }
    [FromQuery(Name = "$deltatoken")] public string? DollarDeltatoken { get; init; }
    [FromQuery(Name = "deltatoken")] public string? BareDeltatoken { get; init; }
    [FromQuery(Name = "$format")] public string? DollarFormat { get; init; }
    [FromQuery(Name = "format")] public string? BareFormat { get; init; }

    public string? Filter => DollarFilter ?? BareFilter;
    public string? Select => DollarSelect ?? BareSelect;
    public string? Orderby => DollarOrderby ?? BareOrderby;
    public string? Top => DollarTop ?? BareTop;
    public string? Skip => DollarSkip ?? BareSkip;
    public string? Skiptoken => DollarSkiptoken ?? BareSkiptoken;
    public string? Count => DollarCount ?? BareCount;
    public string? Expand => DollarExpand ?? BareExpand;
    public string? Compute => DollarCompute ?? BareCompute;
    public string? Search => DollarSearch ?? BareSearch;
    public string? Apply => DollarApply ?? BareApply;
    public string? Deltatoken => DollarDeltatoken ?? BareDeltatoken;
    public string? Format => DollarFormat ?? BareFormat;
}

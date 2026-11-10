// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.ComponentModel.DataAnnotations;

namespace Honua.Protocols.OData;

public sealed class ODataOptions
{
    public const string SectionName = "OData";

    // The page cap exists because an unbounded $top on an ad hoc spatial query can force a database plan that exceeds the request budget.
    [Range(1, 10000)]
    public int MaxPageSize { get; set; } = 1000;

    [Range(1, 100000)]
    public int MaxApplyInputRows { get; set; } = 10000;

    [Range(1, 1000)]
    public int MaxBatchReadOperations { get; set; } = 32;
}

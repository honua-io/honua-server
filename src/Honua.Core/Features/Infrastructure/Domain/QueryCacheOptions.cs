// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Infrastructure.Domain;

public sealed class QueryCacheOptions
{
    // Stay under PostgreSQL's session limit of 1000 prepared statements so other statements still fit.
    public int MaxCachedStatements { get; set; } = 100;

    public int StatementLifetimeMinutes { get; set; } = 30;

    public int MinExecutionsForCaching { get; set; } = 3;

    public bool EnableAutomaticCaching { get; set; } = true;

    public bool EnablePerformanceLogging { get; set; }

    public int CleanupIntervalMinutes { get; set; } = 10;
}

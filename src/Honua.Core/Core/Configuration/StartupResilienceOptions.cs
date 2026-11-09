// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Configuration;

// The default fails startup when the database is down. Lambda and a Development container enable degraded start unless it is set explicitly, for transient connectivity failures and for a skipped-migration schema that is behind this binary.
public sealed record StartupResilienceOptions
{
    public const string SectionName = "Database:StartupResilience";

    public bool DegradedStartEnabled { get; set; }

    public bool RetryMigrationsInBackground { get; set; } = true;

    public TimeSpan RetryBaseDelay { get; set; } = TimeSpan.FromSeconds(2);

    public TimeSpan RetryMaxDelay { get; set; } = TimeSpan.FromSeconds(60);

    public int MaxRetryAttempts { get; set; }
}

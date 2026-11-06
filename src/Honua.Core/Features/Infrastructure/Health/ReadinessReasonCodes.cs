// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Infrastructure.Health;

public static class ReadinessReasonCodes
{
    public const string HeaderName = "X-Honua-Readiness-Reason";

    public const string MigrationsFailed = "migrations-failed";

    public const string MigrationsInProgress = "migrations-in-progress";

    public const string MigrationsNotCompleted = "migrations-not-completed";

    public const string DatabaseStartupDegraded = "database-startup-degraded";

    public const string SchemaFloor = "schema-floor";

    public const string DatabaseUnavailable = "database-unavailable";

    public const string CacheUnavailable = "cache-unavailable";

    public const string FeatureChangeEventStorageUnavailable = "feature-change-event-storage-unavailable";

    public const string AlertDispatchStalled = "alert-dispatch-stalled";

    public const string AlertEvaluationStalled = "alert-evaluation-stalled";

    public const string GeoprocessingOutputStoreAttestationUnavailable = "gp-output-store-attestation-unavailable";
}

// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Server.Features.Admin.OperateFixtures;

internal static class OperateObservabilityFixtureConstants
{
    public const string Profile = "console-testcontainers-v1";
    public const string ServiceId = "operate-fixture-console";
    public const int LayerId = 1;
    public const string CorrelationId = "corr-operate-fixture-001";
    public const string TraceId = "trace-operate-fixture-001";
    public const string Actor = "operator.testcontainers";
    public const string ZoneName = "Honolulu Harbor Testcontainers";
    public const string RuleName = "Harbor Entry Testcontainers";
    public const string SucceededJobId = "operate-fixture-job-succeeded";
    public const string FailedJobId = "operate-fixture-job-failed";
    public const string InvestigationId = "inv-operate-fixture-console";
    public const string FixtureProfileParameter = "honua.fixture.profile";
    public const string FixtureSourceHydratedParameter = "honua.fixture.source_hydrated";

    public const string EnableEnvironmentVariable = "HONUA_ENABLE_OBSERVABILITY_TEST_SEED";
}

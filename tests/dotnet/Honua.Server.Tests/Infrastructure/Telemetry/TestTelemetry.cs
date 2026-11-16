// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Alerts;
using Honua.Infrastructure.Events.Outbox;
using Honua.Server.Features.Streaming;

namespace Honua.Server.Tests.Infrastructure.Telemetry;

internal static class TestTelemetry
{
    // Long-lived factory for throwaway metrics instances. Each metrics instance still gets its own
    // isolated meter; the factory only owns the meters' lifetime for the test process.
    private static readonly TestMeterFactory SharedFactory = new();

    public static FeatureStreamMetrics CreateFeatureStreamMetrics() => new(SharedFactory);

    public static OutboxMetrics CreateOutboxMetrics() => new(SharedFactory);

    public static AlertPipelineMetrics CreateAlertPipelineMetrics() => new(SharedFactory);
}

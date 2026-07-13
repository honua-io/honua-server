// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Alerts;
using Honua.Infrastructure.Events.Outbox;
using Honua.Server.Features.Streaming;

namespace Honua.Server.Tests.Infrastructure.Telemetry;

/// <summary>
/// Test helpers that construct the IMeterFactory-backed metrics singletons (#2802) for tests that
/// exercise their collaborators but do not assert on the metrics themselves. Each call creates a
/// metrics instance on its own meter (the shared factory hands out a fresh meter per
/// <see cref="TestMeterFactory.Create"/>), so no process-global instrument state is shared and no
/// meter registration is required.
/// </summary>
internal static class TestTelemetry
{
    // Long-lived factory for throwaway metrics instances. Each metrics instance still gets its own
    // isolated meter; the factory only owns the meters' lifetime for the test process.
    private static readonly TestMeterFactory SharedFactory = new();

    /// <summary>Creates a <see cref="FeatureStreamMetrics"/> over an isolated test meter.</summary>
    public static FeatureStreamMetrics CreateFeatureStreamMetrics() => new(SharedFactory);

    /// <summary>Creates an <see cref="OutboxMetrics"/> over an isolated test meter.</summary>
    public static OutboxMetrics CreateOutboxMetrics() => new(SharedFactory);

    /// <summary>Creates an <see cref="AlertPipelineMetrics"/> over an isolated test meter.</summary>
    public static AlertPipelineMetrics CreateAlertPipelineMetrics() => new(SharedFactory);
}

// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;

namespace Honua.ControlPlane.Lambda;

internal static class BatchEventParser
{
    internal const string ExpectedSource = "aws.batch";

    internal const string ExpectedDetailType = "Batch Job State Change";

    public static string? ExtractProviderOperationId(BatchJobStateChangeEvent? evt)
    {
        if (evt?.Detail is null)
        {
            return null;
        }

        // Defensive: only act on the event type we expect. EventBridge rule matching already filters
        // to source=aws.batch + detail-type="Batch Job State Change", but a malformed or mis-routed
        // payload must not be coerced into a reconcile.
        if (!string.IsNullOrEmpty(evt.Source)
            && !string.Equals(evt.Source, ExpectedSource, StringComparison.Ordinal))
        {
            return null;
        }

        if (!string.IsNullOrEmpty(evt.DetailType)
            && !string.Equals(evt.DetailType, ExpectedDetailType, StringComparison.Ordinal))
        {
            return null;
        }

        var jobId = evt.Detail.JobId;
        return string.IsNullOrWhiteSpace(jobId) ? null : jobId;
    }

    // Malformed EventBridge JSON returns null instead of throwing, so a bad event is a no-op rather than a Lambda failure.
    public static string? ExtractProviderOperationId(string? eventJson)
    {
        if (string.IsNullOrWhiteSpace(eventJson))
        {
            return null;
        }

        try
        {
            var evt = JsonSerializer.Deserialize(
                eventJson,
                ControlPlaneLambdaJsonContext.Default.BatchJobStateChangeEvent);
            return ExtractProviderOperationId(evt);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

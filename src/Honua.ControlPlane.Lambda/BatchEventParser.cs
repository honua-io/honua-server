// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;

namespace Honua.ControlPlane.Lambda;

/// <summary>
/// Pure parsing of an EventBridge "Batch Job State Change" event into the AWS Batch provider job id
/// (<c>detail.jobId</c>) that the control-plane reconcile entrypoint forwards. Kept side-effect-free
/// and dependency-free so the deserialization contract (sample event JSON → provider id) is fully
/// unit-testable without the Lambda runtime.
/// </summary>
internal static class BatchEventParser
{
    /// <summary>The expected <c>source</c> of a Batch state-change event.</summary>
    internal const string ExpectedSource = "aws.batch";

    /// <summary>The expected <c>detail-type</c> of a Batch state-change event.</summary>
    internal const string ExpectedDetailType = "Batch Job State Change";

    /// <summary>
    /// Extracts the AWS Batch provider job id (<c>detail.jobId</c>) from a deserialized event.
    /// Returns <see langword="null"/> when the event is not a Batch state-change event or carries no
    /// job id; callers treat a null result as "nothing to reconcile" and exit cleanly.
    /// </summary>
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

    /// <summary>
    /// Deserializes raw EventBridge event JSON and extracts the provider job id. Returns
    /// <see langword="null"/> for empty, malformed, or non-matching payloads (never throws on
    /// malformed JSON), so a bad event becomes a clean no-op rather than a Lambda error.
    /// </summary>
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

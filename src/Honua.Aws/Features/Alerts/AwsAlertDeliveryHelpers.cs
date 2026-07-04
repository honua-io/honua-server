// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using Amazon.Runtime;

namespace Honua.Alerts;

/// <summary>
/// Shared exception-classification helpers for the AWS-backed alert delivery
/// sinks (SNS, SQS). Centralizes the retryable/non-retryable decision so both
/// sinks stay consistent.
/// </summary>
internal static class AwsAlertDeliveryHelpers
{
    /// <summary>
    /// Classifies a delivery exception so permanent client errors (HTTP 4xx — for
    /// example a &gt;256KB payload or an invalid message attribute) are reported as
    /// non-retryable. Retrying those never succeeds and only causes the alert
    /// pipeline to loop and lose work. Transient failures (HTTP 5xx, throttling,
    /// request timeouts, and pre-request network errors that never reached AWS)
    /// stay retryable.
    /// </summary>
    internal static bool IsRetryable(Exception ex)
    {
        // AmazonClientException covers SDK-side failures that never reached AWS
        // (DNS, sockets, credential resolution); the request was not rejected, so
        // a retry is worthwhile. It is a sibling of AmazonServiceException in the
        // SDK v4 hierarchy, so check it before the service-exception branch.
        if (ex is not AmazonServiceException serviceEx)
        {
            return true;
        }

        // A zero StatusCode means no HTTP response reached the SDK (transient).
        // 408/429 are explicitly retryable; any other 4xx is a permanent client
        // error (message too large, invalid parameter, access denied) that will
        // never succeed on retry. 5xx is a transient server-side failure.
        if ((int)serviceEx.StatusCode == 0)
        {
            return true;
        }

        if (serviceEx.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests)
        {
            return true;
        }

        return (int)serviceEx.StatusCode >= 500;
    }
}

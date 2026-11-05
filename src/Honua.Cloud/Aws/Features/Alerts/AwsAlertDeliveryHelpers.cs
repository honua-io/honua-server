// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using Amazon.Runtime;

namespace Honua.Alerts;

internal static class AwsAlertDeliveryHelpers
{
    // Permanent client errors are not retried, because retrying a 4xx only loops the alert pipeline.
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

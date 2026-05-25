// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using Honua.ServiceDefaults;

namespace Honua.Server.Features.OpenData;

/// <summary>
/// Activity helpers for open-data publication flows.
/// </summary>
internal static class OpenDataTelemetry
{
    public static Activity? StartActivity(string operation, string route, string method, string? itemId = null)
    {
        var activity = HonuaTelemetry.ActivitySource.StartActivity($"honua.open_data.{operation}", ActivityKind.Internal);
        activity?.SetTag(HonuaTelemetry.Tags.Protocol, "OpenData");
        activity?.SetTag(HonuaTelemetry.Tags.Operation, operation);
        activity?.SetTag("http.route", route);
        activity?.SetTag("http.request.method", method);
        if (!string.IsNullOrWhiteSpace(itemId))
        {
            activity?.SetTag("honua.open_data.item_id", itemId);
        }

        return activity;
    }

    public static void SetResult(Activity? activity, int count)
    {
        HonuaTelemetry.SetSuccess(activity, count);
        activity?.SetTag("honua.open_data.result_count", count);
    }

    public static void SetFailed(Activity? activity, string reason)
    {
        if (activity is null)
        {
            return;
        }

        activity.SetStatus(ActivityStatusCode.Error);
        activity.SetTag(HonuaTelemetry.Tags.Error, true);
        activity.SetTag("honua.open_data.failure_reason", reason);
    }

    public static void RecordException(Activity? activity, Exception exception)
    {
        HonuaTelemetry.RecordException(activity, exception);
    }
}

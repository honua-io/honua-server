// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using Honua.ServiceDefaults;

namespace Honua.Server.Features.Protocols.Stac;

internal static class StacTelemetry
{
    internal static class Operations
    {
        public const string Catalog = "catalog";
        public const string Collections = "collections";
        public const string Collection = "collection";
        public const string Items = "items";
        public const string Item = "item";
        public const string SearchGet = "search.get";
        public const string SearchPost = "search.post";
        public const string SearchExecute = "search.execute";
        public const string SearchWork = "search.work";
    }

    internal static Activity? StartActivity(
        string operation,
        string route,
        string method,
        string? collectionId = null,
        string? itemId = null)
    {
        var activity = HonuaTelemetry.ActivitySource.StartActivity($"honua.stac.{operation}", ActivityKind.Internal);
        activity?.SetTag(HonuaTelemetry.Tags.Protocol, HonuaTelemetry.Protocols.Stac);
        activity?.SetTag(HonuaTelemetry.Tags.Operation, operation);
        activity?.SetTag("http.request.method", method);
        activity?.SetTag("http.route", route);

        if (!string.IsNullOrWhiteSpace(collectionId))
        {
            activity?.SetTag(HonuaTelemetry.Tags.CollectionId, collectionId);
        }

        if (!string.IsNullOrWhiteSpace(itemId))
        {
            activity?.SetTag("honua.stac.item.id", itemId);
        }

        return activity;
    }

    internal static void SetSearchTags(
        Activity? activity,
        int limit,
        int offset,
        int collectionCount,
        int layerCount)
    {
        activity?.SetTag("honua.stac.search.limit", limit);
        activity?.SetTag("honua.stac.search.offset", offset);
        activity?.SetTag("honua.stac.search.collection_count", collectionCount);
        activity?.SetTag("honua.stac.search.layer_count", layerCount);
    }

    internal static void SetResultCount(Activity? activity, int returned, long? matched = null)
    {
        HonuaTelemetry.SetSuccess(activity, returned);
        activity?.SetTag("honua.stac.returned", returned);

        if (matched.HasValue)
        {
            activity?.SetTag("honua.stac.matched", matched.Value);
        }
    }

    internal static void SetFailed(Activity? activity, string reason)
    {
        if (activity is null)
        {
            return;
        }

        activity.SetStatus(ActivityStatusCode.Error);
        activity.SetTag(HonuaTelemetry.Tags.Error, true);
        activity.SetTag("honua.stac.failure_reason", reason);
    }

    internal static void RecordException(Activity? activity, Exception exception)
    {
        HonuaTelemetry.RecordException(activity, exception);
    }
}

// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Honua.Infrastructure.SharedCaching;

// Reuse the exported Honua meter and bounded dimensions; resource identifiers belong in purge records.
internal static class SharedCacheMetrics
{
    private static readonly Meter Meter = new("Honua");
    private static readonly Counter<long> OutputRequests = Meter.CreateCounter<long>(
        "honua_shared_cache_output_requests_total", description: "Origin output-cache outcomes by response class.");
    private static readonly Counter<long> PurgeRequests = Meter.CreateCounter<long>(
        "honua_shared_cache_purge_requests_total", description: "Logical CDN submissions after retries, by reason, scope and outcome.");
    private static readonly Histogram<double> PurgeDuration = Meter.CreateHistogram<double>(
        "honua_shared_cache_purge_duration_seconds", unit: "s", description: "CDN submission duration including bounded retries.");

    internal static void RecordOutput(HttpContext context, string outcome)
        => OutputRequests.Add(1,
            new KeyValuePair<string, object?>("cache_class", SharedCacheResponse.DeclaredClass(context)?.ToString() ?? "unclassified"),
            new KeyValuePair<string, object?>("outcome", outcome));

    internal static void RecordPurge(CdnPurgeCause cause, CdnPurgeSample sample)
    {
        var reason = cause.Reason is "admin" or "edit" or "release" or "operation" ? cause.Reason : "other";
        var scope = sample.Paths.Contains("/*") ? "all"
            : sample.Paths.All(CdnCacheTags.IsTagInvalidation) ? "tags" : "paths";
        TagList tags = new() { { "reason", reason }, { "scope", scope }, { "outcome", sample.Outcome } };
        PurgeRequests.Add(1, tags);
        PurgeDuration.Record(sample.Duration.TotalSeconds, tags);
    }
}

internal readonly record struct CdnPurgeSample(IReadOnlyCollection<string> Paths, string Outcome, TimeSpan Duration);

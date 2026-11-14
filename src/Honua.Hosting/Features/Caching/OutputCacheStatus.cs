// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Text;
using Honua.Infrastructure.SharedCaching;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.Net.Http.Headers;

namespace Honua.Infrastructure.Caching;

// The RFC 9211 Cache-Status entry for this server's own cache: whether a response was replayed from it, rendered on
// a miss, or rendered without it, how long a replayed entry still lives, and the catalog revision it was keyed by.
internal static class OutputCacheStatus
{
    internal const string HeaderName = "Cache-Status";

    internal const string CacheName = "Honua";

    private static readonly object TrackedKey = new();
    private static readonly object HitKey = new();
    private static readonly object RevisionKey = new();
    private static readonly object MetricRecordedKey = new();

    internal static void Track(OutputCacheContext context) => context.HttpContext.Items[TrackedKey] = context;

    internal static void RecordHit(OutputCacheContext context) => context.HttpContext.Items[HitKey] = true;

    internal static void UseCatalogRevision(HttpContext context, string etag) => context.Items[RevisionKey] = etag;

    // Runs when the response starts, after the output cache has copied a stored entry's headers and its Age, and before
    // the shared-cache guard removes that Age.
    internal static void Apply(HttpContext context)
    {
        if (Describe(context) is { } status)
        {
            context.Response.Headers[HeaderName] = status;
            if (context.Items.TryAdd(MetricRecordedKey, true))
            {
                SharedCacheMetrics.RecordOutput(context, Outcome(context)!);
            }
        }
    }

    // The response can start before the output cache's own storage decision is made, so a miss never claims to be
    // stored; a hit and a bypass are known for certain by then.
    internal static string? Describe(HttpContext context)
    {
        var outcome = Outcome(context);
        if (outcome is null)
        {
            return null;
        }
        var status = outcome == "hit"
            ? $"{CacheName}; hit; ttl={Seconds(Lifetime(context, (OutputCacheContext)context.Items[TrackedKey]!) - EntryAge(context.Response))}"
            : $"{CacheName}; fwd={outcome}";
        return WithRevision(context, status);
    }

    private static string? Outcome(HttpContext context)
    {
        var tracked = context.Items.TryGetValue(TrackedKey, out var value) ? value as OutputCacheContext : null;
        if (tracked is null || !RouteUsesOutputCache(context))
        {
            return SharedCacheResponse.DeclaredClass(context) is null ? null : "bypass";
        }
        if (RequestBypassesOutputCache(context))
        {
            return "bypass";
        }
        return tracked.AllowCacheLookup && context.Items.ContainsKey(HitKey) ? "hit" : "miss";
    }

    internal static bool RouteUsesOutputCache(HttpContext context)
        => context.GetEndpoint()?.Metadata is { } metadata
           && (metadata.GetMetadata<IOutputCachePolicy>() is not null || metadata.GetMetadata<OutputCacheAttribute>() is not null);

    private static bool RequestBypassesOutputCache(HttpContext context)
        => !HttpMethods.IsGet(context.Request.Method)
           || context.Request.Headers.ContainsKey(HeaderNames.Range)
           || context.User.Identity?.IsAuthenticated == true
           || BypassOutputCacheOnCredentialedRequestPolicy.CarriesCredential(context);

    private static TimeSpan Lifetime(HttpContext context, OutputCacheContext tracked)
    {
        var lifetime = tracked.ResponseExpirationTimeSpan ?? new OutputCacheOptions().DefaultExpirationTimeSpan;
        return SharedCacheResponse.SharedLifetime(context) is { } cdnLifetime && cdnLifetime < lifetime ? cdnLifetime : lifetime;
    }

    private static TimeSpan EntryAge(HttpResponse response)
        => long.TryParse(response.Headers.Age.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
            ? TimeSpan.FromSeconds(seconds)
            : TimeSpan.Zero;

    private static string WithRevision(HttpContext context, string status)
        => context.Items.TryGetValue(RevisionKey, out var revision) && revision is string etag && Token(etag) is { Length: > 0 } token
            ? $"{status}; detail=\"revision={token}\""
            : status;

    // Only characters that need no escaping inside a structured-field string.
    private static string Token(string etag)
    {
        var builder = new StringBuilder(etag.Length);
        foreach (var character in etag.Where(static c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '_' or ':'))
        {
            builder.Append(character);
        }

        return builder.ToString();
    }

    private static string Seconds(TimeSpan duration)
        => Math.Max(0, (long)duration.TotalSeconds).ToString(CultureInfo.InvariantCulture);
}

// Last of the base policies, so every earlier decision about lookup and storage is already made when it records.
internal sealed class OutputCacheStatusPolicy : IOutputCachePolicy
{
    public ValueTask CacheRequestAsync(OutputCacheContext context, CancellationToken cancellationToken)
    {
        OutputCacheStatus.Track(context);
        return ValueTask.CompletedTask;
    }

    public ValueTask ServeFromCacheAsync(OutputCacheContext context, CancellationToken cancellationToken)
    {
        OutputCacheStatus.RecordHit(context);
        return ValueTask.CompletedTask;
    }

    public ValueTask ServeResponseAsync(OutputCacheContext context, CancellationToken cancellationToken)
        => ValueTask.CompletedTask;
}

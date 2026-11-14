// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Honua.Core.Features.Realtime;
using Honua.Geoprocessing;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Capabilities;
using Honua.Infrastructure.Middleware;
using Honua.Infrastructure.Models;
using Honua.ServiceDefaults;
using Microsoft.AspNetCore.Mvc;

namespace Honua.Server.Features.Streaming;

internal static partial class RealtimeNotificationEndpoints
{
    public static void MapRealtimeNotificationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v{version:apiVersion}/streaming")
            .WithCapabilityGate("realtime.feature-streams")
            .WithApiVersionSet().HasApiVersion(1, 0).WithTags("Streaming");
        group.MapGet("/layers", HandleLayersAsync)
            .AddEndpointFilter<LiveStreamAuthorizationFilter>()
            .WithMetadata(LongLivedStreamEndpointMetadata.Instance)
            .WithDisplayName("Stream Layer Changes")
            .AllowAnonymous();
        group.MapGet("/jobs", HandleJobsAsync)
            .AddEndpointFilter<LiveStreamAuthorizationFilter>()
            .WithMetadata(LongLivedStreamEndpointMetadata.Instance)
            .WithDisplayName("Stream Job Status")
            .AllowAnonymous();
    }

    internal static async Task<IResult> HandleJobsAsync(HttpContext context,
        [FromServices] IRealtimeFanoutProvider provider,
        [FromServices] IGeoprocessingJobService jobs)
    {
        var jobIds = context.Request.Query["jobId"].ToArray();
        if (jobIds.Length is < 1 or > 50 || jobIds.Any(string.IsNullOrWhiteSpace) ||
            jobIds.Any(id => id!.Length > 256) ||
            !TryParseCursor(context, RealtimeFamily.Jobs, out var after))
        {
            return Results.BadRequest();
        }
        var scopes = jobIds.Select(id => id!).Distinct(StringComparer.Ordinal).ToArray();
        try
        {
            foreach (var jobId in scopes)
            {
                await jobs.GetJobAsync(jobId, context.User, context.RequestAborted).ConfigureAwait(false);
            }
        }
        catch (GeoprocessingNotFoundException)
        {
            return Results.NotFound();
        }
        catch (GeoprocessingAuthorizationException)
        {
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        await StreamAsync(context, provider, RealtimeFamily.Jobs, scopes, after, async (item, token) =>
        {
            if (item.Kind != "job.status" || !scopes.Contains(item.ScopeKey, StringComparer.Ordinal))
            {
                return true;
            }
            // Re-evaluate current ownership and authorization for replay and live delivery.
            await jobs.GetJobAsync(item.ScopeKey, context.User, token).ConfigureAwait(false);
            await WriteFrameAsync(context.Response, item.Position, "job.status", writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("jobId", item.ScopeKey);
                foreach (var name in new[] { "status", "updatedAt" })
                {
                    if (item.Payload.TryGetProperty(name, out var value))
                    {
                        writer.WritePropertyName(name);
                        value.WriteTo(writer);
                    }
                }
                writer.WriteEndObject();
            }, token).ConfigureAwait(false);
            return true;
        }).ConfigureAwait(false);
        return Results.Empty;
    }

    internal static bool TryParseCursor(HttpContext context, RealtimeFamily family, out long? after)
    {
        after = null;
        var values = context.Request.Headers["Last-Event-ID"];
        if (values.Count > 1)
        {
            return false;
        }
        var value = values.ToString();
        if (value.Length == 0)
        {
            return true;
        }
        var prefix = family.Name + ":";
        if (!value.StartsWith(prefix, StringComparison.Ordinal) ||
            !long.TryParse(value.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var sequence))
        {
            return false;
        }
        after = sequence;
        return true;
    }

    internal static async Task StreamAsync(HttpContext context, IRealtimeFanoutProvider provider,
        RealtimeFamily family, IReadOnlyCollection<string> scopes, long? after,
        Func<RealtimeEvent, CancellationToken, Task<bool>> deliver)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        var channel = Channel.CreateBounded<RealtimeDelivery>(256);
        await using var subscription = provider.Subscribe(new RealtimeSubscriptionRequest(
            family, scopes, after, new ClosingWriter(channel.Writer, lifetime)));
        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-store, no-transform";
        context.Response.Headers["X-Accel-Buffering"] = "no";
        await context.Response.StartAsync(lifetime.Token).ConfigureAwait(false);
        var heartbeat = HeartbeatAsync(channel.Writer, lifetime.Token);
        try
        {
            await foreach (var delivery in channel.Reader.ReadAllAsync(lifetime.Token).ConfigureAwait(false))
            {
                switch (delivery)
                {
                    case RealtimeDelivery.Event item:
                        if (!await deliver(item.Value, lifetime.Token).ConfigureAwait(false))
                        {
                            return;
                        }
                        break;
                    case RealtimeDelivery.Reset reset:
                        await WriteFrameAsync(context.Response, null, "reset", writer =>
                        {
                            writer.WriteStartObject();
                            writer.WriteString("reason", reset.Reason);
                            writer.WriteNumber("head", reset.Head);
                            writer.WriteEndObject();
                        }, lifetime.Token).ConfigureAwait(false);
                        return;
                    case RealtimeDelivery.Heartbeat:
                        await context.Response.WriteAsync(": hb\n\n", lifetime.Token).ConfigureAwait(false);
                        await context.Response.Body.FlushAsync(lifetime.Token).ConfigureAwait(false);
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch (GeoprocessingAuthorizationException)
        {
            // Authorization changed after admission: close without sending another job frame.
        }
        catch (GeoprocessingNotFoundException)
        {
        }
        finally
        {
            await lifetime.CancelAsync().ConfigureAwait(false);
            await heartbeat.ConfigureAwait(false);
        }
    }

    internal static async Task WriteFrameAsync(HttpResponse response, RealtimePosition? position,
        string kind, Action<Utf8JsonWriter> writePayload, CancellationToken token)
    {
        using var payload = new MemoryStream();
        using (var writer = new Utf8JsonWriter(payload))
        {
            writePayload(writer);
        }
        var header = position is { } cursor ? $"id: {cursor}\nevent: {kind}\ndata: " : $"event: {kind}\ndata: ";
        await response.Body.WriteAsync(Encoding.UTF8.GetBytes(header), token).ConfigureAwait(false);
        await response.Body.WriteAsync(payload.GetBuffer().AsMemory(0, checked((int)payload.Length)), token).ConfigureAwait(false);
        await response.Body.WriteAsync("\n\n"u8.ToArray(), token).ConfigureAwait(false);
        await response.Body.FlushAsync(token).ConfigureAwait(false);
    }

    private static async Task HeartbeatAsync(ChannelWriter<RealtimeDelivery> writer, CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        try
        {
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                writer.TryWrite(RealtimeDelivery.Heartbeat.Instance);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
    }

    private sealed class ClosingWriter(ChannelWriter<RealtimeDelivery> writer, CancellationTokenSource lifetime)
        : ChannelWriter<RealtimeDelivery>
    {
        public override bool TryWrite(RealtimeDelivery item) => writer.TryWrite(item);
        public override ValueTask<bool> WaitToWriteAsync(CancellationToken cancellationToken = default)
            => writer.WaitToWriteAsync(cancellationToken);
        public override bool TryComplete(Exception? error = null)
        {
            var completed = writer.TryComplete(error);
            if (error is not null)
            {
                lifetime.Cancel();
            }
            return completed;
        }
    }
}

// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json;
using System.Threading.Channels;
using Honua.Core.Features.Realtime;
using Honua.Infrastructure.Monitoring;

namespace Honua.Server.Features.Admin;

internal sealed class AdminLiveStream(IServiceProvider services, IHostEnvironment environment,
    MigrationState migrationState)
{
    private const int SubscriberCapacity = 256;

    public async Task HandleAsync(HttpContext context)
    {
        var topics = ParseTopics(context.Request.Query["topics"].ToString());
        var cursor = context.Request.Headers["Last-Event-ID"].ToString();
        var (validCursor, after) = ParseCursor(cursor);
        if (topics is null || !validCursor)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var provider = services.GetRequiredService<IRealtimeFanoutProvider>();
        // Seal the live boundary before returning status so the first client-triggered edit is replayable.
        after ??= (await provider.GetWindowAsync(RealtimeFamily.Admin, context.RequestAborted).ConfigureAwait(false)).Head;
        var channel = Channel.CreateBounded<RealtimeDelivery>(new BoundedChannelOptions(SubscriberCapacity)
        {
            SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait
        });
        await using var subscription = provider.Subscribe(new RealtimeSubscriptionRequest(
            RealtimeFamily.Admin, topics, after, channel.Writer));
        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache, no-transform";
        context.Response.Headers["X-Accel-Buffering"] = "no";

        try
        {
            await WriteAsync(context, "id: admin:" + after.Value.ToString(CultureInfo.InvariantCulture) + "\nevent: status\ndata: " +
                JsonSerializer.Serialize(CreateStatus(topics), AdminRealtimeJsonContext.Default.AdminRealtimeStatus) + "\n\n").ConfigureAwait(false);
            await StreamWithResetAsync(context, channel.Reader, subscription).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is OperationCanceledException or IOException or ChannelClosedException)
        {
            // The stream ends when the client disconnects or its bounded subscription closes.
        }
    }

    private static async Task StreamWithResetAsync(HttpContext context, ChannelReader<RealtimeDelivery> reader,
        IRealtimeSubscription subscription)
    {
        try
        {
            await StreamAsync(context, reader).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is
            RealtimeSubscriptionClosedException { Reason: RealtimeResetReasons.SlowConsumer } or
            ChannelClosedException { InnerException: RealtimeSubscriptionClosedException { Reason: RealtimeResetReasons.SlowConsumer } })
        {
            await WriteAsync(context, Format(new RealtimeDelivery.Reset(RealtimeResetReasons.SlowConsumer,
                subscription.Position))).ConfigureAwait(false);
        }
    }

    private AdminRealtimeStatus CreateStatus(string[] topics)
        => new(migrationState.IsFailed ? "degraded" : "online", Environment.MachineName,
            environment.EnvironmentName, migrationState.Status.ToString().ToLowerInvariant(),
            migrationState.IsReady, DateTimeOffset.UtcNow, topics);

    internal static string[]? ParseTopics(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return AdminRealtimeContract.Topics;
        }
        var topics = value.Split(',', StringSplitOptions.TrimEntries).Distinct(StringComparer.Ordinal).ToArray();
        return topics.All(topic => AdminRealtimeContract.Topics.Contains(topic, StringComparer.Ordinal)) ? topics : null;
    }

    internal static (bool Valid, long? After) ParseCursor(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return (true, null);
        }
        if (!value.StartsWith("admin:", StringComparison.Ordinal) ||
            !long.TryParse(value.AsSpan(6), NumberStyles.None, CultureInfo.InvariantCulture, out var sequence))
        {
            return (false, null);
        }
        return (true, sequence);
    }

    private static async Task StreamAsync(HttpContext context, ChannelReader<RealtimeDelivery> reader)
    {
        using var heartbeat = new PeriodicTimer(TimeSpan.FromSeconds(15));
        var tick = heartbeat.WaitForNextTickAsync(context.RequestAborted).AsTask();
        var available = reader.WaitToReadAsync(context.RequestAborted).AsTask();
        while (!context.RequestAborted.IsCancellationRequested)
        {
            var completed = await Task.WhenAny(tick, available).ConfigureAwait(false);
            if (completed == tick)
            {
                if (!await tick.ConfigureAwait(false)) { return; }
                await WriteAsync(context, ": heartbeat\n\n").ConfigureAwait(false);
                tick = heartbeat.WaitForNextTickAsync(context.RequestAborted).AsTask();
                continue;
            }
            if (!await available.ConfigureAwait(false)) { return; }
            while (reader.TryRead(out var delivery))
            {
                await WriteAsync(context, Format(delivery)).ConfigureAwait(false);
            }
            available = reader.WaitToReadAsync(context.RequestAborted).AsTask();
        }
    }

    private static string Format(RealtimeDelivery delivery) => delivery switch
    {
        RealtimeDelivery.Event item => $"id: {item.Value.Position}\nevent: {item.Value.Kind}\ndata: {JsonSerializer.Serialize(item.Value.Payload, AdminRealtimeJsonContext.Default.JsonElement)}\n\n",
        RealtimeDelivery.Reset reset => "event: reset\ndata: " + JsonSerializer.Serialize(
            new AdminLiveReset(reset.Reason, reset.Head), AdminRealtimeJsonContext.Default.AdminLiveReset) + "\n\n",
        _ => ": heartbeat\n\n"
    };

    private static async Task WriteAsync(HttpContext context, string frame)
    {
        await context.Response.WriteAsync(frame, context.RequestAborted).ConfigureAwait(false);
        await context.Response.Body.FlushAsync(context.RequestAborted).ConfigureAwait(false);
    }
}

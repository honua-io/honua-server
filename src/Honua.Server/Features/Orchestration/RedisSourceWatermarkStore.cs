// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Core.Features.Migration.Watermark;
using StackExchange.Redis;

namespace Honua.Server.Features.Orchestration;

/// <summary>
/// Redis-backed durable store for per-pipeline+source high-water marks. Advancing a
/// timestamp-based watermark is monotonic: a competing replica or an out-of-order completion
/// can never rewind the mark past a value already persisted, so a later run cannot re-pull
/// records an earlier run already extracted. The mark is stored as a single string value so an
/// optimistic compare-and-set guards concurrent advances without per-field null ambiguity.
/// </summary>
internal sealed class RedisSourceWatermarkStore(IConnectionMultiplexer redis) : ISourceWatermarkStore
{
    private const string WatermarkKeyPrefix = "orchestration:watermark:";

    private readonly IDatabase _database = redis.GetDatabase();

    public async Task<SourceWatermark?> GetAsync(
        string pipelineId,
        string sourceId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipelineId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        cancellationToken.ThrowIfCancellationRequested();

        var payload = await _database.StringGetAsync(GetKey(pipelineId, sourceId)).ConfigureAwait(false);
        return payload.HasValue ? Deserialize(pipelineId, sourceId, payload!) : null;
    }

    public async Task<SourceWatermark> AdvanceAsync(
        SourceWatermark watermark,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(watermark);
        ArgumentException.ThrowIfNullOrWhiteSpace(watermark.PipelineId);
        ArgumentException.ThrowIfNullOrWhiteSpace(watermark.SourceId);
        cancellationToken.ThrowIfCancellationRequested();

        var key = GetKey(watermark.PipelineId, watermark.SourceId);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var current = await _database.StringGetAsync(key).ConfigureAwait(false);
            var existing = current.HasValue
                ? Deserialize(watermark.PipelineId, watermark.SourceId, current!)
                : null;

            // Monotonic guard: never move a timestamp watermark backwards.
            var effective = MergeMonotonic(existing, watermark);
            var serialized = Serialize(effective);

            if (!current.HasValue)
            {
                // Create only if still absent; a racing creator forces a re-read.
                if (await _database.StringSetAsync(key, serialized, when: When.NotExists).ConfigureAwait(false))
                {
                    return effective;
                }

                continue;
            }

            // Compare-and-set against the exact bytes we read so a concurrent advance cannot be
            // clobbered; on contention we re-read and re-apply the monotonic merge.
            var transaction = _database.CreateTransaction();
            transaction.AddCondition(Condition.StringEqual(key, current));
            _ = transaction.StringSetAsync(key, serialized);
            if (await transaction.ExecuteAsync().ConfigureAwait(false))
            {
                return effective;
            }
        }
    }

    public async Task ClearAsync(
        string pipelineId,
        string sourceId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipelineId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        cancellationToken.ThrowIfCancellationRequested();

        await _database.KeyDeleteAsync(GetKey(pipelineId, sourceId)).ConfigureAwait(false);
    }

    private static SourceWatermark MergeMonotonic(SourceWatermark? existing, SourceWatermark requested)
    {
        if (existing is null)
        {
            return requested;
        }

        var existingTimestamp = existing.AsTimestamp();
        var requestedTimestamp = requested.AsTimestamp();

        // Both timestamp-based: only advance when strictly newer; otherwise keep the persisted
        // mark (refreshing UpdatedAt so callers can observe the touch).
        if (existingTimestamp is { } prior && requestedTimestamp is { } next && next <= prior)
        {
            return existing with { UpdatedAt = requested.UpdatedAt };
        }

        return requested;
    }

    private static string Serialize(SourceWatermark watermark)
        => JsonSerializer.Serialize(
            new WatermarkPayload(watermark.Kind, watermark.Value, watermark.UpdatedAt),
            WatermarkJsonContext.Default.WatermarkPayload);

    private static SourceWatermark Deserialize(string pipelineId, string sourceId, string payload)
    {
        WatermarkPayload? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize(payload, WatermarkJsonContext.Default.WatermarkPayload);
        }
        catch (JsonException)
        {
            parsed = null;
        }

        return new SourceWatermark
        {
            PipelineId = pipelineId,
            SourceId = sourceId,
            Kind = parsed?.Kind ?? WatermarkKind.EditTimestamp,
            Value = string.IsNullOrEmpty(parsed?.Value) ? null : parsed.Value,
            UpdatedAt = parsed?.UpdatedAt
        };
    }

    private static RedisKey GetKey(string pipelineId, string sourceId)
        => string.Concat(WatermarkKeyPrefix, pipelineId, ":", sourceId);

    internal sealed record WatermarkPayload(
        WatermarkKind Kind,
        string? Value,
        DateTimeOffset? UpdatedAt);
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(RedisSourceWatermarkStore.WatermarkPayload))]
internal sealed partial class WatermarkJsonContext : JsonSerializerContext
{
}

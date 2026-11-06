// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Tiles;
using StackExchange.Redis;

namespace Honua.Infrastructure.Caching;

// Resume state must not live on one pod, so a Redis multiplexer overrides the in-memory default and abandoned generations expire.
internal sealed partial class RedisTileCacheGenerationCheckpointStore : ITileCacheGenerationCheckpointStore
{
    private const string KeyPrefix = "honua:tile-cache:generation:checkpoint:";

    // Abandoned generations (process crashed before delete) self-expire so stale resume state
    // never accumulates. 24h matches the tile-operation progress/request retention window.
    private static readonly TimeSpan CheckpointTtl = TimeSpan.FromHours(24);

    private readonly IConnectionMultiplexer _redis;
    private readonly ILogger<RedisTileCacheGenerationCheckpointStore> _logger;

    public RedisTileCacheGenerationCheckpointStore(
        IConnectionMultiplexer redis,
        ILogger<RedisTileCacheGenerationCheckpointStore> logger)
    {
        _redis = redis ?? throw new ArgumentNullException(nameof(redis));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async ValueTask SaveAsync(TileCacheGenerationCheckpoint checkpoint, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        cancellationToken.ThrowIfCancellationRequested();

        var sanitized = TileCacheGenerationCheckpointBounds.Sanitize(checkpoint);
        var payload = JsonSerializer.Serialize(
            sanitized,
            TileCacheGenerationCheckpointJsonContext.Default.TileCacheGenerationCheckpoint);

        await _redis.GetDatabase()
            .StringSetAsync(BuildKey(sanitized.GenerationId), payload, CheckpointTtl)
            .ConfigureAwait(false);
    }

    public async ValueTask<TileCacheGenerationCheckpoint?> LoadAsync(string generationId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(generationId);
        cancellationToken.ThrowIfCancellationRequested();

        var value = await _redis.GetDatabase().StringGetAsync(BuildKey(generationId)).ConfigureAwait(false);
        if (value.IsNullOrEmpty)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize(
                (string)value!,
                TileCacheGenerationCheckpointJsonContext.Default.TileCacheGenerationCheckpoint);
        }
        catch (JsonException ex)
        {
            // Preserve malformed state and fail the read. Seed/warm callers intentionally wrap
            // loads in their best-effort adapter, while destructive delete callers must fail
            // closed rather than erase unknown reservation accounting and restart their budget.
            Log.CorruptCheckpointReadFailed(_logger, generationId, ex);
            throw new InvalidDataException(
                $"Tile-cache checkpoint '{generationId}' is corrupt and cannot be resumed safely.",
                ex);
        }
    }

    public async ValueTask<bool> DeleteAsync(string generationId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(generationId);
        cancellationToken.ThrowIfCancellationRequested();

        return await _redis.GetDatabase().KeyDeleteAsync(BuildKey(generationId)).ConfigureAwait(false);
    }

    private static string BuildKey(string generationId) => $"{KeyPrefix}{generationId}";

    private static partial class Log
    {
        [LoggerMessage(9230, LogLevel.Warning,
            "Could not read corrupt tile-cache generation checkpoint for generation {GenerationId}.")]
        public static partial void CorruptCheckpointReadFailed(ILogger logger, string generationId, Exception exception);
    }
}

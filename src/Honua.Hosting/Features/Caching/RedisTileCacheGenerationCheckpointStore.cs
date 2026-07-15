// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Tiles;
using StackExchange.Redis;

namespace Honua.Infrastructure.Caching;

/// <summary>
/// Redis-backed <see cref="ITileCacheGenerationCheckpointStore"/> (issue #2661). Durable,
/// cross-node resume state for bounded generated tile-cache seed/warm generations must not live
/// on a single serving pod's local memory, so when a Redis multiplexer is present this binding
/// overrides the in-memory default. Each generation's checkpoint is stored under a stable,
/// prefixed key with a bounded TTL so abandoned generations self-expire. Payloads are truncated
/// to the deterministic upper bound enforced by <see cref="TileCacheGenerationCheckpointBounds"/>
/// before serialization, so persisted state stays release-safe regardless of gridset size.
/// </summary>
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

    /// <inheritdoc />
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

    /// <inheritdoc />
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
            // A malformed payload is unrecoverable resume state; drop it and start clean rather
            // than surfacing a parse error into the seed loop.
            Log.CorruptCheckpointDropped(_logger, generationId, ex);
            await _redis.GetDatabase().KeyDeleteAsync(BuildKey(generationId)).ConfigureAwait(false);
            return null;
        }
    }

    /// <inheritdoc />
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
            "Dropped a corrupt tile-cache generation checkpoint for generation {GenerationId}.")]
        public static partial void CorruptCheckpointDropped(ILogger logger, string generationId, Exception exception);
    }
}

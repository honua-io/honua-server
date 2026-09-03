using System.Collections.Concurrent;
using Google.Protobuf;
using Proto = Geospatial.V1;
using StackExchange.Redis;

namespace Honua.Server.Features.Protocols.Grpc;

/// <summary>At-most-once store for gRPC ApplyEdits retries, shared through Redis when configured.</summary>
internal sealed class GrpcApplyEditsIdempotencyStore(IConnectionMultiplexer? multiplexer = null)
{
    private static readonly TimeSpan Window = TimeSpan.FromHours(24);
    private static readonly TimeSpan ReservationWindow = TimeSpan.FromSeconds(60);
    private const byte PendingMarker = 0xFF;
    private const string RedisPrefix = "honua:grpc:apply-edits:idempotency:";
    private readonly IDatabase? _redis = multiplexer?.GetDatabase();
    private readonly ConcurrentDictionary<string, Entry> _responses = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.Ordinal);

    public async Task<Lease> EnterAsync(string key, CancellationToken cancellationToken)
    {
        var gate = _locks.GetOrAdd(key, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_redis is not null)
            {
                var redisKey = RedisPrefix + key;
                while (true)
                {
                    var payload = await _redis.StringGetAsync(redisKey).ConfigureAwait(false);
                    if (payload.HasValue)
                    {
                        var bytes = (byte[])payload!;
                        if (bytes.Length > 0 && bytes[0] != PendingMarker)
                        {
                            return new Lease(gate, Proto.ApplyEditsResponse.Parser.ParseFrom(bytes));
                        }

                        await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    var reservation = new byte[] { PendingMarker };
                    if (await _redis.StringSetAsync(redisKey, reservation, ReservationWindow, When.NotExists).ConfigureAwait(false))
                    {
                        return new Lease(gate, response: null);
                    }
                }
            }

            if (_responses.TryGetValue(key, out var entry) && entry.ExpiresAt > DateTimeOffset.UtcNow)
            {
                return new Lease(gate, entry.Response.Clone());
            }

            _responses.TryRemove(key, out _);
            return new Lease(gate, response: null);
        }
        catch
        {
            gate.Release();
            throw;
        }
    }

    public void Set(string key, Proto.ApplyEditsResponse response)
    {
        if (_redis is not null)
        {
            try
            {
                _redis.StringSet(RedisPrefix + key, response.ToByteArray(), Window);
            }
            catch (OutOfMemoryException)
            {
                throw;
            }
            catch (Exception)
            {
                // The edit has already committed; a Redis outage must not turn that
                // successful operation into a retry-triggering gRPC failure.
            }
        }

        _responses[key] = new Entry(response.Clone(), DateTimeOffset.UtcNow.Add(Window));
    }

    public sealed class Lease : IDisposable
    {
        private readonly SemaphoreSlim _gate;
        internal Lease(SemaphoreSlim gate, Proto.ApplyEditsResponse? response)
        {
            _gate = gate;
            Response = response;
        }

        public Proto.ApplyEditsResponse? Response { get; }
        public void Dispose() => _gate.Release();
    }

    private sealed record Entry(Proto.ApplyEditsResponse Response, DateTimeOffset ExpiresAt);
}

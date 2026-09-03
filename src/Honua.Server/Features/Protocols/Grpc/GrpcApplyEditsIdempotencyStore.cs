using System.Collections.Concurrent;
using Google.Protobuf;
using Proto = Geospatial.V1;

namespace Honua.Server.Features.Protocols.Grpc;

/// <summary>Process-local at-most-once store for gRPC ApplyEdits retries.</summary>
internal sealed class GrpcApplyEditsIdempotencyStore
{
    private static readonly TimeSpan Window = TimeSpan.FromHours(24);
    private readonly ConcurrentDictionary<string, Entry> _responses = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.Ordinal);

    public async Task<Lease> EnterAsync(string key, CancellationToken cancellationToken)
    {
        var gate = _locks.GetOrAdd(key, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (_responses.TryGetValue(key, out var entry) && entry.ExpiresAt > DateTimeOffset.UtcNow)
        {
            return new Lease(gate, entry.Response.Clone());
        }

        _responses.TryRemove(key, out _);
        return new Lease(gate, response: null);
    }

    public void Set(string key, Proto.ApplyEditsResponse response)
        => _responses[key] = new Entry(response.Clone(), DateTimeOffset.UtcNow.Add(Window));

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

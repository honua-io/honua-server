// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Abstractions;
using Microsoft.Extensions.Caching.Memory;

namespace Honua.Core.Features.Edit.Receipts;

// Coordinates byte receipts without choosing a protocol's codec, caller scope or failure policy.
public sealed class FeatureEditReceiptCache : IDisposable
{
    public static readonly TimeSpan DefaultResponseWindow = TimeSpan.FromHours(24);
    public const long DefaultLocalResponseBudgetBytes = 64L * 1024 * 1024;
    private const int LocalEntryOverheadBytes = 256;
    private readonly IFeatureEditIdempotencyReceipts? _durableReceipts;
    private readonly TimeSpan _responseWindow;
    private readonly MemoryCache _localResponses;
    private readonly Dictionary<string, KeyGate> _gates = new(StringComparer.Ordinal);

    public FeatureEditReceiptCache(IFeatureEditIdempotencyReceipts? durableReceipts, Limits limits)
    {
        _durableReceipts = durableReceipts;
        _responseWindow = limits.ResponseWindow;
        _localResponses = new MemoryCache(new MemoryCacheOptions
        {
            SizeLimit = limits.BudgetBytes,
            ExpirationScanFrequency = _responseWindow < TimeSpan.FromMinutes(1) ? _responseWindow : TimeSpan.FromMinutes(1),
        });
    }

    public readonly record struct Limits(TimeSpan ResponseWindow, long BudgetBytes);
    public enum ReceiptState { Absent, Recorded, LocalRecorded, Unknown }
    public readonly record struct Receipt(ReadOnlyMemory<byte> Payload, ReceiptState State);

    internal int GateCount
    {
        get
        {
            lock (_gates)
            {
                return _gates.Count;
            }
        }
    }

    internal int LocalResponseCount => _localResponses.Count;
    public DateTimeOffset ResponseExpiresAt => TimeProvider.System.GetUtcNow() + _responseWindow;

    public async Task<Entry> EnterAsync(string scope, CancellationToken cancellationToken)
    {
        var gate = await AcquireGateAsync(scope, cancellationToken).ConfigureAwait(false);
        try
        {
            // A local committed result wins; otherwise a durable receipt wins over a local
            // unknown outcome or a failure that committed nothing.
            if (_localResponses.TryGetValue(scope, out object? cached) && cached is byte[] local)
            {
                return new Entry(this, new Lease(scope, gate), Recorded(local, ReceiptState.LocalRecorded));
            }

            var durable = await ReadDurableAsync(scope, cancellationToken).ConfigureAwait(false);
            if (durable.State == ReceiptState.Absent)
            {
                durable = cached switch
                {
                    AmbiguousLocal => new Receipt(default, ReceiptState.Unknown),
                    UncommittedLocal failure => Recorded(failure.Payload, ReceiptState.LocalRecorded),
                    _ => durable,
                };
            }

            return new Entry(this, new Lease(scope, gate), durable);
        }
        catch
        {
            ReleaseGate(scope, gate);
            throw;
        }
    }

    public void Dispose() => _localResponses.Dispose();

    private static Receipt Recorded(ReadOnlyMemory<byte> payload, ReceiptState state) => new(payload.ToArray(), state);

    private async Task<Receipt> ReadDurableAsync(string scope, CancellationToken cancellationToken)
    {
        if (_durableReceipts is null)
        {
            return default;
        }

        var payload = await _durableReceipts.TryReadAsync(scope, cancellationToken).ConfigureAwait(false);
        return payload is { Length: > 0 } bytes ? Recorded(bytes, ReceiptState.Recorded) : default;
    }

    private async Task<KeyGate> AcquireGateAsync(string scope, CancellationToken cancellationToken)
    {
        KeyGate? gate;
        lock (_gates)
        {
            if (!_gates.TryGetValue(scope, out gate))
            {
                gate = new KeyGate();
                _gates.Add(scope, gate);
            }

            gate.References++;
        }

        try
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            return gate;
        }
        catch
        {
            DropGateReference(scope, gate);
            throw;
        }
    }

    private void ReleaseGate(string scope, KeyGate gate)
    {
        gate.Release();
        DropGateReference(scope, gate);
    }

    // A gate leaves the map only when no holder or waiter references it, so callers
    // queued on one key always share one semaphore.
    private void DropGateReference(string scope, KeyGate gate)
    {
        lock (_gates)
        {
            if (--gate.References == 0)
            {
                _gates.Remove(scope);
            }
        }
    }

    private void StoreLocal(string scope, object entry, long size)
        => _localResponses.Set(scope, entry, new MemoryCacheEntryOptions
        {
            Size = size + LocalEntryOverheadBytes,
            AbsoluteExpirationRelativeToNow = _responseWindow,
        });

    private sealed class AmbiguousLocal
    {
        public static readonly AmbiguousLocal Instance = new();
    }

    private sealed record UncommittedLocal(byte[] Payload);

    internal sealed class KeyGate() : SemaphoreSlim(1, 1)
    {
        public int References { get; set; }
    }

    internal readonly record struct Lease(string Scope, KeyGate Gate);

    // Holds the scope's local gate until disposed. Byte buffers are copied at the cache boundary.
    public sealed class Entry : IDisposable
    {
        private readonly FeatureEditReceiptCache _receipts;
        private readonly Lease _lease;
        private bool _disposed;

        internal Entry(FeatureEditReceiptCache receipts, Lease lease, Receipt recorded)
        {
            _receipts = receipts;
            _lease = lease;
            Recorded = recorded;
        }

        public string Scope => _lease.Scope;
        public Receipt Recorded { get; }

        public Task<Receipt> ReadCommittedAsync(CancellationToken cancellationToken)
            => _receipts.ReadDurableAsync(Scope, cancellationToken);

        public void Complete(ReadOnlyMemory<byte> payload, long responseSize)
            => _receipts.StoreLocal(Scope, payload.ToArray(), responseSize);

        public void CompleteUncommitted(ReadOnlyMemory<byte> payload, long responseSize)
            => _receipts.StoreLocal(Scope, new UncommittedLocal(payload.ToArray()), responseSize);

        public void RetainAmbiguous() => _receipts.StoreLocal(Scope, AmbiguousLocal.Instance, size: 0);

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _receipts.ReleaseGate(Scope, _lease.Gate);
        }
    }
}

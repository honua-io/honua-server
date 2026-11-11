// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Google.Protobuf;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Infrastructure.Security;
using Microsoft.Extensions.Caching.Memory;
using Proto = Geospatial.V1;

namespace Honua.Server.Features.Protocols.Grpc;

// Keyed applyEdits retries across nodes are decided by the receipt row the edit transaction
// commits: a second commit for the same scope is rolled back by that row's key and replays it.
// Within one node, a per-scope gate serializes duplicates and a bounded local memory replays
// results and keeps an unknown outcome from running again.
internal sealed class GrpcApplyEditsReceipts : IDisposable
{
    internal static readonly TimeSpan DefaultResponseWindow = TimeSpan.FromHours(24);
    internal const long DefaultLocalResponseBudgetBytes = 64L * 1024 * 1024;

    private const byte ReceiptMarker = 0x01;
    private const int LocalEntryOverheadBytes = 256;

    private readonly IFeatureEditIdempotencyReceipts? _durableReceipts;
    private readonly TimeSpan _responseWindow;
    private readonly MemoryCache _localResponses;
    private readonly Dictionary<string, KeyGate> _gates = new(StringComparer.Ordinal);

    public GrpcApplyEditsReceipts(IFeatureEditIdempotencyReceipts? durableReceipts = null)
        : this(durableReceipts, new LocalResultLimits(DefaultResponseWindow, DefaultLocalResponseBudgetBytes))
    {
    }

    internal GrpcApplyEditsReceipts(IFeatureEditIdempotencyReceipts? durableReceipts, LocalResultLimits limits)
    {
        _durableReceipts = durableReceipts;
        _responseWindow = limits.ResponseWindow;
        _localResponses = new MemoryCache(new MemoryCacheOptions
        {
            SizeLimit = limits.BudgetBytes,
            ExpirationScanFrequency = _responseWindow < TimeSpan.FromMinutes(1) ? _responseWindow : TimeSpan.FromMinutes(1),
        });
    }

    internal readonly record struct LocalResultLimits(TimeSpan ResponseWindow, long BudgetBytes);

    public readonly record struct ScopeKey(string? ServiceId, int LayerId, string ClientKey);

    // An undecodable payload must not be executed again.
    internal readonly record struct DurableEditReceipt(Proto.ApplyEditsResponse? Response, bool OutcomeUnknown);

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

    internal DateTimeOffset ResponseExpiresAt => TimeProvider.System.GetUtcNow() + _responseWindow;

    internal static byte[] EncodeReceipt(Proto.ApplyEditsResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        var body = response.ToByteArray();
        var receipt = new byte[body.Length + 1];
        receipt[0] = ReceiptMarker;
        body.CopyTo(receipt, 1);
        return receipt;
    }

    internal static DurableEditReceipt DecodeReceipt(ReadOnlySpan<byte> receipt)
    {
        if (receipt.Length < 2 || receipt[0] != ReceiptMarker)
        {
            return new DurableEditReceipt(Response: null, OutcomeUnknown: true);
        }

        try
        {
            return new DurableEditReceipt(
                Proto.ApplyEditsResponse.Parser.ParseFrom(receipt[1..].ToArray()),
                OutcomeUnknown: false);
        }
        catch (InvalidProtocolBufferException)
        {
            return new DurableEditReceipt(Response: null, OutcomeUnknown: true);
        }
    }

    // An identity that cannot be bound to a durable actor gets no retry scope.
    public static string? CreateScope(HttpContext? httpContext, ScopeKey key)
    {
        if (IdempotencyCaller.Resolve(httpContext) is not { } caller)
        {
            return null;
        }

        var canonicalScope = new StringBuilder("grpc-apply-edits/v2");
        AppendComponent(canonicalScope, key.ServiceId ?? string.Empty);
        AppendComponent(canonicalScope, key.LayerId.ToString(CultureInfo.InvariantCulture));
        AppendComponent(canonicalScope, caller.Tenant);
        AppendComponent(canonicalScope, caller.Actor);
        AppendComponent(canonicalScope, key.ClientKey);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalScope.ToString())));
    }

    public async Task<Entry> EnterAsync(string scope, CancellationToken cancellationToken)
    {
        var gate = await AcquireGateAsync(scope, cancellationToken).ConfigureAwait(false);
        try
        {
            // A result this node committed wins over a database read. A committed database
            // receipt wins over this node's ambiguous marker and over a failure that
            // committed nothing, since another node may have committed the key since.
            if (_localResponses.TryGetValue(scope, out object? cached) && cached is Proto.ApplyEditsResponse local)
            {
                return new Entry(this, scope, gate, new DurableEditReceipt(local.Clone(), OutcomeUnknown: false));
            }

            var durable = await ReadDurableAsync(scope, cancellationToken).ConfigureAwait(false);
            if (durable is { Response: null, OutcomeUnknown: false })
            {
                durable = cached switch
                {
                    AmbiguousLocal => new DurableEditReceipt(Response: null, OutcomeUnknown: true),
                    UncommittedLocal failure => new DurableEditReceipt(failure.Response.Clone(), OutcomeUnknown: false),
                    _ => durable,
                };
            }

            return new Entry(this, scope, gate, durable);
        }
        catch
        {
            ReleaseGate(scope, gate);
            throw;
        }
    }

    public void Dispose() => _localResponses.Dispose();

    private static void AppendComponent(StringBuilder builder, string value)
        => builder.Append('|').Append(value.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(value);

    private async Task<DurableEditReceipt> ReadDurableAsync(string scope, CancellationToken cancellationToken)
    {
        if (_durableReceipts is null)
        {
            return default;
        }

        var payload = await _durableReceipts.TryReadAsync(scope, cancellationToken).ConfigureAwait(false);
        return payload is { Length: > 0 } bytes ? DecodeReceipt(bytes.Span) : default;
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

    private sealed record UncommittedLocal(Proto.ApplyEditsResponse Response);

    internal sealed class KeyGate() : SemaphoreSlim(1, 1)
    {
        public int References { get; set; }
    }

    // Holds the scope's gate on this node until disposed.
    public sealed class Entry : IDisposable
    {
        private readonly GrpcApplyEditsReceipts _receipts;
        private readonly KeyGate _gate;
        private bool _disposed;

        internal Entry(GrpcApplyEditsReceipts receipts, string scope, KeyGate gate, DurableEditReceipt recorded)
        {
            _receipts = receipts;
            _gate = gate;
            Scope = scope;
            Response = recorded.Response;
            OutcomeUnknown = recorded.OutcomeUnknown;
        }

        public string Scope { get; }

        public Proto.ApplyEditsResponse? Response { get; }

        // A keyed write that may have committed must not be executed again, and it is not a stored failure.
        public bool OutcomeUnknown { get; }

        public Task<DurableEditReceipt> ReadCommittedAsync(CancellationToken cancellationToken)
            => _receipts.ReadDurableAsync(Scope, cancellationToken);

        public void Complete(Proto.ApplyEditsResponse response)
        {
            var copy = response.Clone();
            _receipts.StoreLocal(Scope, copy, copy.CalculateSize());
        }

        // A result that committed nothing and left no receipt is replayed only while no
        // other node has committed a receipt for the key.
        public void CompleteUncommitted(Proto.ApplyEditsResponse response)
        {
            var copy = response.Clone();
            _receipts.StoreLocal(Scope, new UncommittedLocal(copy), copy.CalculateSize());
        }

        public void RetainAmbiguous() => _receipts.StoreLocal(Scope, AmbiguousLocal.Instance, size: 0);

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _receipts.ReleaseGate(Scope, _gate);
        }
    }
}

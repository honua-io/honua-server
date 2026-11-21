// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Google.Protobuf;
using Honua.Core.Features.Edit.Receipts;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Infrastructure.Security;

namespace Honua.Server.Features.Protocols.Grpc;

// Keyed applyEdits retries across nodes are decided by the receipt row the edit transaction
// commits: a second commit for the same scope is rolled back by that row's key and replays it.
// Within one node, a per-scope gate serializes duplicates and a bounded local memory replays
// results and keeps an unknown outcome from running again.
internal abstract class GrpcEditReceipts<TResponse> : IDisposable where TResponse : class, IMessage<TResponse>
{
    internal static readonly TimeSpan DefaultResponseWindow = FeatureEditReceiptCache.DefaultResponseWindow;
    internal const long DefaultLocalResponseBudgetBytes = FeatureEditReceiptCache.DefaultLocalResponseBudgetBytes;
    private readonly FeatureEditReceiptCache _cache;

    protected GrpcEditReceipts(IFeatureEditIdempotencyReceipts? durableReceipts = null)
        : this(durableReceipts, new LocalResultLimits(DefaultResponseWindow, DefaultLocalResponseBudgetBytes))
    {
    }

    protected GrpcEditReceipts(IFeatureEditIdempotencyReceipts? durableReceipts, LocalResultLimits limits)
    {
        _cache = new FeatureEditReceiptCache(durableReceipts,
            new FeatureEditReceiptCache.Limits(limits.ResponseWindow, limits.BudgetBytes));
    }

    internal readonly record struct LocalResultLimits(TimeSpan ResponseWindow, long BudgetBytes);

    public readonly record struct ScopeKey(string? ServiceId, int LayerId, string ClientKey);

    // An undecodable payload must not be executed again.
    internal readonly record struct DurableEditReceipt(TResponse? Response, ReceiptOutcome Outcome)
    {
        internal bool OutcomeUnknown => Outcome == ReceiptOutcome.Unknown;

        internal string? RequestFingerprint { get; init; }
    }

    internal enum ReceiptOutcome { NotRecorded, Recorded, Unknown }

    internal int GateCount => _cache.GateCount;
    internal int LocalResponseCount => _cache.LocalResponseCount;
    internal DateTimeOffset ResponseExpiresAt => _cache.ResponseExpiresAt;

    protected abstract DurableEditReceipt DecodeDurable(ReadOnlyMemory<byte> receipt);
    protected virtual DurableEditReceipt DecodeLocal(ReadOnlyMemory<byte> receipt) => DecodeDurable(receipt);
    protected abstract byte[] EncodeLocal(TResponse response, string? fingerprint);

    private DurableEditReceipt Decode(FeatureEditReceiptCache.Receipt receipt)
        => receipt.State switch
        {
            FeatureEditReceiptCache.ReceiptState.Recorded => DecodeDurable(receipt.Payload),
            FeatureEditReceiptCache.ReceiptState.LocalRecorded => DecodeLocal(receipt.Payload),
            FeatureEditReceiptCache.ReceiptState.Unknown => new(null, ReceiptOutcome.Unknown),
            _ => default,
        };

    // An identity that cannot be bound to a durable actor gets no retry scope.
    protected static string? CreateScope(HttpContext? httpContext, ScopeKey key, string prefix)
    {
        if (IdempotencyCaller.Resolve(httpContext) is not { } caller)
        {
            return null;
        }

        var canonicalScope = new StringBuilder(prefix);
        AppendComponent(canonicalScope, key.ServiceId ?? string.Empty);
        AppendComponent(canonicalScope, key.LayerId.ToString(CultureInfo.InvariantCulture));
        AppendComponent(canonicalScope, caller.Tenant);
        AppendComponent(canonicalScope, caller.Actor);
        AppendComponent(canonicalScope, key.ClientKey);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalScope.ToString())));
    }

    public async Task<Entry> EnterAsync(string scope, CancellationToken cancellationToken)
    {
        var entry = await _cache.EnterAsync(scope, cancellationToken).ConfigureAwait(false);
        try
        {
            return new Entry(this, entry, Decode(entry.Recorded));
        }
        catch
        {
            entry.Dispose();
            throw;
        }
    }

    public void Dispose() => _cache.Dispose();

    private static void AppendComponent(StringBuilder builder, string value)
        => builder.Append('|').Append(value.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(value);

    // Holds the scope's gate on this node until disposed.
    public sealed class Entry : IDisposable
    {
        private readonly GrpcEditReceipts<TResponse> _receipts;
        private readonly FeatureEditReceiptCache.Entry _entry;

        internal Entry(GrpcEditReceipts<TResponse> receipts, FeatureEditReceiptCache.Entry entry, DurableEditReceipt recorded)
        {
            _receipts = receipts;
            _entry = entry;
            Scope = entry.Scope;
            Response = recorded.Response;
            OutcomeUnknown = recorded.OutcomeUnknown;
            RequestFingerprint = recorded.RequestFingerprint;
        }

        public string Scope { get; }

        internal string? RequestFingerprint { get; }

        public TResponse? Response { get; }

        // A keyed write that may have committed must not be executed again, and it is not a stored failure.
        public bool OutcomeUnknown { get; }

        public async Task<DurableEditReceipt> ReadCommittedAsync(CancellationToken cancellationToken)
            => _receipts.Decode(await _entry.ReadCommittedAsync(cancellationToken).ConfigureAwait(false));

        public void Complete(TResponse response, string? fingerprint = null)
            => _entry.Complete(_receipts.EncodeLocal(response, fingerprint), response.CalculateSize());

        public void CompleteUncommitted(TResponse response, string? fingerprint = null)
            => _entry.CompleteUncommitted(_receipts.EncodeLocal(response, fingerprint), response.CalculateSize());

        public void RetainAmbiguous() => _entry.RetainAmbiguous();
        public void Dispose() => _entry.Dispose();
    }
}

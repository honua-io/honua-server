// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Google.Protobuf;
using Honua.Core.Features.Edit.Receipts;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Infrastructure.Security;
using Proto = Geospatial.V1;

namespace Honua.Server.Features.Protocols.Grpc;

// The adapter owns caller scoping and protobuf encoding; Core coordinates opaque byte receipts.
internal sealed class GrpcApplyEditsReceipts : IDisposable
{
    internal static readonly TimeSpan DefaultResponseWindow = FeatureEditReceiptCache.DefaultResponseWindow;
    internal const long DefaultLocalResponseBudgetBytes = FeatureEditReceiptCache.DefaultLocalResponseBudgetBytes;
    private const byte ReceiptMarker = 0x01;
    private readonly FeatureEditReceiptCache _cache;

    public GrpcApplyEditsReceipts(IFeatureEditIdempotencyReceipts? durableReceipts = null)
        : this(durableReceipts, new LocalResultLimits(DefaultResponseWindow, DefaultLocalResponseBudgetBytes))
    {
    }

    internal GrpcApplyEditsReceipts(IFeatureEditIdempotencyReceipts? durableReceipts, LocalResultLimits limits)
    {
        _cache = new FeatureEditReceiptCache(durableReceipts,
            new FeatureEditReceiptCache.Limits(limits.ResponseWindow, limits.BudgetBytes));
    }

    internal readonly record struct LocalResultLimits(TimeSpan ResponseWindow, long BudgetBytes);
    public readonly record struct ScopeKey(string? ServiceId, int LayerId, string ClientKey);
    internal readonly record struct DurableEditReceipt(Proto.ApplyEditsResponse? Response, bool OutcomeUnknown);
    internal int GateCount => _cache.GateCount;
    internal int LocalResponseCount => _cache.LocalResponseCount;
    internal DateTimeOffset ResponseExpiresAt => _cache.ResponseExpiresAt;

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
        var entry = await _cache.EnterAsync(scope, cancellationToken).ConfigureAwait(false);
        try
        {
            return new Entry(entry);
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

    private static DurableEditReceipt Decode(FeatureEditReceiptCache.Receipt receipt)
        => receipt.State switch
        {
            FeatureEditReceiptCache.ReceiptState.Recorded => DecodeReceipt(receipt.Payload.Span),
            FeatureEditReceiptCache.ReceiptState.LocalRecorded => DecodeLocal(receipt.Payload.Span),
            FeatureEditReceiptCache.ReceiptState.Unknown => new DurableEditReceipt(null, OutcomeUnknown: true),
            _ => default,
        };

    private static DurableEditReceipt DecodeLocal(ReadOnlySpan<byte> payload)
        => payload.Length == 1 && payload[0] == ReceiptMarker
            ? new DurableEditReceipt(new Proto.ApplyEditsResponse(), OutcomeUnknown: false)
            : DecodeReceipt(payload);

    public sealed class Entry : IDisposable
    {
        private readonly FeatureEditReceiptCache.Entry _entry;
        internal Entry(FeatureEditReceiptCache.Entry entry)
        {
            _entry = entry;
            var recorded = Decode(entry.Recorded);
            Response = recorded.Response;
            OutcomeUnknown = recorded.OutcomeUnknown;
        }

        public string Scope => _entry.Scope;
        public Proto.ApplyEditsResponse? Response { get; }
        public bool OutcomeUnknown { get; }

        public async Task<DurableEditReceipt> ReadCommittedAsync(CancellationToken cancellationToken)
            => Decode(await _entry.ReadCommittedAsync(cancellationToken).ConfigureAwait(false));

        public void Complete(Proto.ApplyEditsResponse response)
            => _entry.Complete(EncodeReceipt(response), response.CalculateSize());

        public void CompleteUncommitted(Proto.ApplyEditsResponse response)
            => _entry.CompleteUncommitted(EncodeReceipt(response), response.CalculateSize());

        public void RetainAmbiguous() => _entry.RetainAmbiguous();
        public void Dispose() => _entry.Dispose();
    }
}

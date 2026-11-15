// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Google.Protobuf;
using Honua.Core.Features.FeatureStore.Abstractions;
using Proto = Geospatial.V1;

namespace Honua.Server.Features.Protocols.Grpc;

internal sealed class GrpcApplyEditsReceipts : GrpcEditReceipts<Proto.ApplyEditsResponse>
{
    public GrpcApplyEditsReceipts(IFeatureEditIdempotencyReceipts? durableReceipts = null) : base(durableReceipts)
    {
    }

    internal GrpcApplyEditsReceipts(IFeatureEditIdempotencyReceipts? durableReceipts, LocalResultLimits limits)
        : base(durableReceipts, limits)
    {
    }

    public static string? CreateScope(HttpContext? context, ScopeKey key)
        => CreateScope(context, key, "grpc-apply-edits/v2");

    internal static byte[] EncodeReceipt(Proto.ApplyEditsResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        var payload = response.ToByteArray();
        return [0x01, .. payload];
    }

    internal static DurableEditReceipt DecodeReceipt(ReadOnlySpan<byte> receipt)
    {
        if (receipt.Length < 2 || receipt[0] != 0x01)
        {
            return new(null, ReceiptOutcome.Unknown);
        }
        try
        {
            return new(Proto.ApplyEditsResponse.Parser.ParseFrom(receipt[1..].ToArray()), ReceiptOutcome.Recorded);
        }
        catch (InvalidProtocolBufferException)
        {
            return new(null, ReceiptOutcome.Unknown);
        }
    }

    protected override DurableEditReceipt DecodeDurable(ReadOnlyMemory<byte> receipt) => DecodeReceipt(receipt.Span);
}

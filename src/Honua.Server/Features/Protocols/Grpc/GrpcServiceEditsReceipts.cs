// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Cryptography;
using Google.Protobuf;
using Honua.Core.Features.FeatureStore.Abstractions;
using Proto = Geospatial.V1;

namespace Honua.Server.Features.Protocols.Grpc;

internal sealed class GrpcServiceEditsReceipts(IFeatureEditIdempotencyReceipts? receipts = null)
    : GrpcEditReceipts<Proto.ApplyServiceEditsResponse>(receipts)
{
    private const int FingerprintLength = 32;

    internal static string Fingerprint(Proto.ApplyServiceEditsRequest request)
    {
        var canonical = request.Clone();
        canonical.IdempotencyKey = string.Empty;
        var bytes = new byte[canonical.CalculateSize()];
        using var output = new CodedOutputStream(bytes) { Deterministic = true };
        canonical.WriteTo(output);
        output.CheckNoSpaceLeft();
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    public static string? CreateScope(HttpContext? context, ScopeKey key)
        => CreateScope(context, key, "grpc-apply-service-edits/v1");

    internal static byte[] EncodeReceipt(Proto.ApplyServiceEditsResponse response, string fingerprint)
        => [0x02, .. Convert.FromHexString(fingerprint), .. response.ToByteArray()];

    protected override DurableEditReceipt DecodeDurable(ReadOnlyMemory<byte> receipt)
    {
        if (receipt.Length < FingerprintLength + 1 || receipt.Span[0] != 0x02)
        {
            return new(null, ReceiptOutcome.Unknown);
        }
        try
        {
            return new(Proto.ApplyServiceEditsResponse.Parser.ParseFrom(receipt[(FingerprintLength + 1)..].ToArray()), ReceiptOutcome.Recorded)
            {
                RequestFingerprint = Convert.ToHexStringLower(receipt.Span.Slice(1, FingerprintLength))
            };
        }
        catch (InvalidProtocolBufferException)
        {
            return new(null, ReceiptOutcome.Unknown);
        }
    }
}

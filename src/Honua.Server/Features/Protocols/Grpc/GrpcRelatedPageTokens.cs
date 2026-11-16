// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Buffers.Binary;
using System.Security.Cryptography;
using Grpc.Core;
using Honua.Core.Features.FeatureStore.Domain;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;

namespace Honua.Server.Features.Protocols.Grpc;

internal sealed class GrpcRelatedPageTokens(IDataProtectionProvider protection, string binding)
{
    private const int PayloadLength = 26;
    private const int LifetimeSeconds = 900;

    internal string Encode(RelatedPageCursor position, DateTimeOffset now)
    {
        var payload = new byte[PayloadLength];
        payload[0] = 1;
        BinaryPrimitives.WriteInt64BigEndian(payload.AsSpan(1), now.ToUnixTimeSeconds());
        BinaryPrimitives.WriteInt64BigEndian(payload.AsSpan(9), position.OriginId);
        payload[17] = position.RelatedId.HasValue ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteInt64BigEndian(payload.AsSpan(18), position.RelatedId.GetValueOrDefault());
        return WebEncoders.Base64UrlEncode(Protector().Protect(payload));
    }

    internal RelatedPageCursor Decode(string token, DateTimeOffset now)
    {
        if (token.Length is 0 or > 4096)
        {
            throw InvalidToken();
        }
        try
        {
            var payload = Protector().Unprotect(WebEncoders.Base64UrlDecode(token));
            if (payload.Length != PayloadLength || payload[0] != 1 || payload[17] > 1)
            {
                throw InvalidToken();
            }
            var issued = BinaryPrimitives.ReadInt64BigEndian(payload.AsSpan(1));
            var current = now.ToUnixTimeSeconds();
            if (issued > current || issued < current - LifetimeSeconds)
            {
                throw InvalidToken();
            }
            return new RelatedPageCursor(BinaryPrimitives.ReadInt64BigEndian(payload.AsSpan(9)),
                payload[17] == 1 ? BinaryPrimitives.ReadInt64BigEndian(payload.AsSpan(18)) : null);
        }
        catch (Exception exception) when (exception is FormatException or CryptographicException)
        {
            throw InvalidToken();
        }
    }

    private IDataProtector Protector() =>
        protection.CreateProtector("Honua.Grpc.RelatedRecords.v1", binding);

    private static RpcException InvalidToken() =>
        new(new Status(StatusCode.InvalidArgument, "Invalid or expired page_token."));
}

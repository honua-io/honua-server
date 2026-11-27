// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Npgsql;

namespace Honua.Db.Postgres.Features.Infrastructure.Caching;

internal sealed partial class PreparedStatementCache
{
    internal readonly record struct StatementHash
    {
        private const int StackAllocUtf8Threshold = 512;

        public ulong P0 { get; init; }
        public ulong P1 { get; init; }
        public ulong P2 { get; init; }
        public ulong P3 { get; init; }

        public static StatementHash Compute(string sql)
        {
            Span<byte> digest = stackalloc byte[SHA256.HashSizeInBytes];
            var maxUtf8Bytes = Encoding.UTF8.GetMaxByteCount(sql.Length);
            if (maxUtf8Bytes <= StackAllocUtf8Threshold)
            {
                Span<byte> utf8Buffer = stackalloc byte[StackAllocUtf8Threshold];
                var written = Encoding.UTF8.GetBytes(sql, utf8Buffer);
                SHA256.TryHashData(utf8Buffer[..written], digest, out _);
            }
            else
            {
                var rented = ArrayPool<byte>.Shared.Rent(maxUtf8Bytes);
                try
                {
                    var written = Encoding.UTF8.GetBytes(sql, rented);
                    SHA256.TryHashData(rented.AsSpan(0, written), digest, out _);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(rented);
                }
            }

            return new StatementHash
            {
                P0 = BinaryPrimitives.ReadUInt64LittleEndian(digest[..8]),
                P1 = BinaryPrimitives.ReadUInt64LittleEndian(digest[8..16]),
                P2 = BinaryPrimitives.ReadUInt64LittleEndian(digest[16..24]),
                P3 = BinaryPrimitives.ReadUInt64LittleEndian(digest[24..32])
            };
        }

        public string ToHexString()
        {
            Span<byte> digest = stackalloc byte[SHA256.HashSizeInBytes];
            BinaryPrimitives.WriteUInt64LittleEndian(digest[..8], P0);
            BinaryPrimitives.WriteUInt64LittleEndian(digest[8..16], P1);
            BinaryPrimitives.WriteUInt64LittleEndian(digest[16..24], P2);
            BinaryPrimitives.WriteUInt64LittleEndian(digest[24..32], P3);
            return Convert.ToHexString(digest);
        }
    }

    private static void ConfigureAndTrackParameters(
        NpgsqlCommand destination,
        Action<NpgsqlCommand> configureParameters,
        StatementMetrics? metrics)
    {
        // Configure parameters once and reuse the scratch command for both value binding and
        // distribution tracking. CommandText must be populated because parameter callbacks may
        // inspect it before binding conditional parameters such as OFFSET.
        using var configured = new NpgsqlCommand { CommandText = destination.CommandText };
        configureParameters(configured);
        ApplyParameterValues(destination, configured);

        if (metrics != null)
        {
            RecordParameterDistributions(metrics, configured);
        }
    }

    private static void ApplyParameterValues(NpgsqlCommand destination, NpgsqlCommand configured)
    {
        for (var index = 0; index < configured.Parameters.Count; index++)
        {
            var source = configured.Parameters[index];

            if (destination.Parameters.Count > index)
            {
                var target = destination.Parameters[index];
                target.Value = source.Value;
                target.ParameterName = source.ParameterName;
                target.NpgsqlDbType = source.NpgsqlDbType;
                target.DbType = source.DbType;
                target.Direction = source.Direction;
                target.Size = source.Size;
                target.Precision = source.Precision;
                target.Scale = source.Scale;
            }
            else
            {
                destination.Parameters.Add(CloneParameter(source, includeValue: true));
            }
        }
    }

    internal bool TryAddCachedStatementForTest(int connectionId, string sql, CachedStatement cachedStatement)
    {
        var key = (connectionId, StatementHash.Compute(sql));
        if (_cache.TryAdd(key, cachedStatement))
        {
            TrackConnectionStatement(key);
            _connectionCounts.AddOrUpdate(connectionId, 1, static (_, count) => count + 1);
            return true;
        }

        return false;
    }

    internal bool IsPreparationSupportedForTest(NpgsqlConnection connection)
        => IsPreparationSupported(connection);

    internal void RemoveStaleCachedStatementForTest(int connectionId, string sql, CachedStatement expected)
        => RemoveAndDisposeCachedStatement((connectionId, StatementHash.Compute(sql)), expected);

    internal CachedStatement? GetCachedStatementForTest(int connectionId, string sql)
        => _cache.TryGetValue((connectionId, StatementHash.Compute(sql)), out var cached) ? cached : null;

    internal int GetConnectionCountForTest(int connectionId)
        => GetConnectionCacheCount(connectionId);
}

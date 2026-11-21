// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.AuditLog.Abstractions;
using Honua.TestKit.Attributes;

namespace Honua.Db.Postgres.Tests.Features.AuditLog;

// A batch is written under one chain lock, and batches racing single-event writers still
// leave one linear chain that verifies.
public sealed partial class PostgresAuditLogChainTests
{
    [IntegrationTest]
    public async Task RecordBatchAsync_RacingSingleWriters_KeepsOneVerifiedChain()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresAuditLogChainTests));
        try
        {
            await EnsureAuditLogTableAsync(schema);
            var writers = Enumerable.Range(0, 8).Select(writer => writer % 2 == 0
                ? KeyedSink(schema).RecordBatchAsync(BatchOf(writer, 25))
                : KeyedSink(schema).RecordAsync(Event($"single-{writer}", "mcp.tool.call")));

            await Task.WhenAll(writers);

            var rows = await ReadChainAsync(schema);
            rows.Should().HaveCount(4 * 25 + 4);
            rows.Skip(1).Zip(rows).Should().OnlyContain(pair => pair.First.PrevHash == pair.Second.EntryHash);
            var report = await KeyedVerifier(schema).VerifyAsync();
            report.Verified.Should().BeTrue();
            report.RowsChecked.Should().Be(rows.Count);
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    private static AuditEvent[] BatchOf(int writer, int count) =>
        [.. Enumerable.Range(0, count).Select(i => Event($"batch-{writer}-{i}", "mcp.resource.read"))];
}

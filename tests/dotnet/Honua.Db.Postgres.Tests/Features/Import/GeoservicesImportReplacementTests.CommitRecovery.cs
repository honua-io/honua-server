// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Npgsql;

namespace Honua.Db.Postgres.Tests.Features.Import;

public sealed partial class GeoservicesImportReplacementTests
{
    public enum CommitRecoveryMode { Create, Overwrite }

    [Theory]
    [InlineData(CommitRecoveryMode.Create)]
    [InlineData(CommitRecoveryMode.Overwrite)]
    public async Task ImportLayerAsync_RestartAfterCommit_ReportsCommittedRowsWithoutReplaying(CommitRecoveryMode mode)
    {
        var schema = await fixture.CreateIsolatedSchemaAsync("ImportRestartAfterCommit");
        try
        {
            using var firstSource = new ReplacementFeatureServerHandler(["CCC"]);
            var request = BuildRequest(schema, mode == CommitRecoveryMode.Overwrite) with { JobId = Guid.NewGuid().ToString("N") };
            var first = await CreateService(firstSource, schema).ImportLayerAsync(request, new InterruptAfterCommit());
            first.DataCommitted.Should().BeTrue();

            // A new worker sees a changed source after losing the committed worker's final outcome.
            using var changedSource = new ReplacementFeatureServerHandler(["DDD"]);
            var recovered = await CreateService(changedSource, schema).ImportLayerAsync(request);

            recovered.DataCommitted.Should().BeTrue();
            recovered.NeedsReview.Should().BeTrue();
            recovered.FeatureCount.Should().Be(1);
            (await ReadCodesAsync(schema)).Should().Equal("CCC");
            changedSource.ObjectIdWindowsServed.Should().BeEmpty();
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    [Fact]
    public async Task ImportLayerAsync_CommitRejected_RetryDoesNotSeeRolledBackReceipt()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync("ImportReceiptRollback");
        try
        {
            using var source = new ReplacementFeatureServerHandler(["CCC"]);
            var request = BuildRequest(schema, overwrite: false) with { JobId = Guid.NewGuid().ToString("N") };
            var rejected = await CreateService(source, schema).ImportLayerAsync(request with
            {
                CommitParticipant = (_, _) => throw new InvalidOperationException("Execution lease lost")
            });
            rejected.DataCommitted.Should().BeFalse();
            (await CreateService(source, schema).RecoverCommittedImportAsync(request)).Should().BeNull();

            using var retrySource = new ReplacementFeatureServerHandler(["DDD"]);
            var retried = await CreateService(retrySource, schema).ImportLayerAsync(request);

            retried.Success.Should().BeTrue(retried.ErrorMessage);
            (await ReadCodesAsync(schema)).Should().Equal("DDD");
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    [Fact]
    public async Task RecoverCommittedImportAsync_CommittedRows_RecoversWithoutSourceAccess()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync("ImportReceiptLookup");
        try
        {
            using var source = new ReplacementFeatureServerHandler(["CCC"]);
            var request = BuildRequest(schema, overwrite: false) with { JobId = Guid.NewGuid().ToString("N") };
            var imported = await CreateService(source, schema).ImportLayerAsync(request);
            imported.DataCommitted.Should().BeTrue();
            using var unavailableSource = new UnavailableRecoverySource();

            var recovered = await CreateService(unavailableSource, schema).RecoverCommittedImportAsync(request);

            recovered.Should().NotBeNull();
            recovered!.DataCommitted.Should().BeTrue();
            recovered.NeedsReview.Should().BeTrue();
            recovered.FeatureCount.Should().Be(1);
            (await ReadCodesAsync(schema)).Should().Equal("CCC");

            var mismatchedIdentity = async () => await CreateService(unavailableSource, schema)
                .RecoverCommittedImportAsync(request with { LayerId = request.LayerId + 1 });
            await mismatchedIdentity.Should().ThrowAsync<InvalidOperationException>();
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    [Fact]
    public async Task RecoverCommittedImportAsync_ActiveWriter_RefusesUncommittedClassification()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync("ImportRecoveryActiveWriter");
        try
        {
            await using var writer = await fixture.DataSource.OpenConnectionAsync();
            await using var acquire = new NpgsqlCommand("SELECT pg_advisory_lock(hashtextextended(@key, 0))", writer);
            acquire.Parameters.AddWithValue("key", $"honua.geoservices-import:\"{schema}\".\"{TableName}\"");
            await acquire.ExecuteNonQueryAsync();
            try
            {
                using var source = new UnavailableRecoverySource();
                var request = BuildRequest(schema, overwrite: false) with { JobId = Guid.NewGuid().ToString("N") };
                var recover = async () => await CreateService(source, schema).RecoverCommittedImportAsync(request);
                await recover.Should().ThrowAsync<InvalidOperationException>().WithMessage("*active target writer*");
            }
            finally
            {
                await using var release = new NpgsqlCommand("SELECT pg_advisory_unlock_all()", writer);
                await release.ExecuteNonQueryAsync();
            }
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    private sealed class UnavailableRecoverySource : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Recovery must not contact the source.");
    }

    private sealed class InterruptAfterCommit : IProgress<GeoservicesImportProgress>
    {
        public void Report(GeoservicesImportProgress value)
        {
            if (value.Status is GeoservicesImportStatus.Completed or GeoservicesImportStatus.NeedsReview)
            {
                throw new IOException("Worker lost final outcome after data commit");
            }
        }
    }
}

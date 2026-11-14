// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Protocols.Ogc.Classic.Wfs20.Services;
using Honua.Server.Tests.Infrastructure;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Npgsql;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

[Protocol(TestProtocols.Wfs20)]
[Operation(Operations.TestInfrastructure)]
public sealed class PostgresWfsStoredQueryStoreTests(DatabaseFixtureAdapter database) : IClassFixture<DatabaseFixtureAdapter>
{
    [IntegrationTest]
    public async Task Create_SameIdOnTwoNodes_SecondIsRefused()
    {
        await using var harness = await CreateHarnessAsync();
        var scope = Scope();

        var outcomes = await Task.WhenAll(
            harness.CreateWfsStoredQueryStore().TryCreateAsync(Entry(scope, "urn:test:parcels", "first"), CancellationToken.None),
            harness.CreateWfsStoredQueryStore().TryCreateAsync(Entry(scope, "URN:TEST:PARCELS", "second"), CancellationToken.None));

        outcomes.Should().BeEquivalentTo([WfsStoredQueryCreateResult.Created, WfsStoredQueryCreateResult.DuplicateId]);
        (await harness.CreateWfsStoredQueryStore().ListAsync(scope, CancellationToken.None)).Should().ContainSingle();
    }

    [IntegrationTest]
    public async Task Create_FullScope_IsRefusedAndOtherScopesAreUnaffected()
    {
        await using var harness = await CreateHarnessAsync();
        var store = harness.CreateWfsStoredQueryStore();
        var scope = Scope();
        await harness.ExecuteAsync(
            $$"""
            INSERT INTO {{harness.Table("wfs_stored_queries")}} (scope, query_id, definition)
            SELECT @scope, 'urn:fill:' || n, '{}'::json FROM generate_series(1, @count) AS n
            """,
            new NpgsqlParameter("scope", scope),
            new NpgsqlParameter("count", IWfsStoredQueryStore.MaxPerScope));

        (await store.TryCreateAsync(Entry(scope, "urn:test:one-more", "one more"), CancellationToken.None))
            .Should().Be(WfsStoredQueryCreateResult.ScopeFull);
        (await store.TryCreateAsync(Entry(Scope(), "urn:test:one-more", "one more"), CancellationToken.None))
            .Should().Be(WfsStoredQueryCreateResult.Created);
    }

    [IntegrationTest]
    public async Task Create_ConcurrentDistinctIdsAtCapacity_AdmitsOnlyOne()
    {
        await using var harness = await CreateHarnessAsync();
        var scope = Scope();
        await harness.ExecuteAsync(
            $$"""
            INSERT INTO {{harness.Table("wfs_stored_queries")}} (scope, query_id, definition)
            SELECT @scope, 'urn:fill:' || n, '{}'::json FROM generate_series(1, @count) AS n
            """,
            new NpgsqlParameter("scope", scope),
            new NpgsqlParameter("count", IWfsStoredQueryStore.MaxPerScope - 1));

        var outcomes = await Task.WhenAll(Enumerable.Range(0, 16).Select(index =>
            harness.CreateWfsStoredQueryStore().TryCreateAsync(
                Entry(scope, $"urn:concurrent:{index}", "concurrent"), CancellationToken.None)));

        outcomes.Count(outcome => outcome == WfsStoredQueryCreateResult.Created).Should().Be(1);
        outcomes.Count(outcome => outcome == WfsStoredQueryCreateResult.ScopeFull).Should().Be(15);
        (await harness.CreateWfsStoredQueryStore().ListAsync(scope, CancellationToken.None))
            .Should().HaveCount(IWfsStoredQueryStore.MaxPerScope);
    }

    [IntegrationTest]
    public async Task GetAndDelete_IdInAnotherCase_FindTheStoredQueryOnlyInItsScope()
    {
        await using var harness = await CreateHarnessAsync();
        var store = harness.CreateWfsStoredQueryStore();
        var scope = Scope();
        await store.TryCreateAsync(Entry(scope, "urn:test:roads", "roads"), CancellationToken.None);

        (await store.GetAsync(new WfsStoredQueryKey(scope, "URN:Test:Roads"), CancellationToken.None))
            .Should().Contain("roads");
        (await store.GetAsync(new WfsStoredQueryKey(Scope(), "urn:test:roads"), CancellationToken.None)).Should().BeNull();
        (await store.DeleteAsync(new WfsStoredQueryKey(Scope(), "urn:test:roads"), CancellationToken.None)).Should().BeFalse();
        (await store.DeleteAsync(new WfsStoredQueryKey(scope, "URN:TEST:ROADS"), CancellationToken.None)).Should().BeTrue();
        (await store.ListAsync(scope, CancellationToken.None)).Should().BeEmpty();
    }

    private Task<ControlPlaneStateHarness> CreateHarnessAsync()
        => ControlPlaneStateHarness.CreateAsync(database, nameof(PostgresWfsStoredQueryStoreTests));

    private static string Scope() => $"tenant-{Guid.NewGuid():N}";

    private static WfsStoredQueryEntry Entry(string scope, string queryId, string title)
        => new(new WfsStoredQueryKey(scope, queryId), $$"""{"Id":"{{queryId}}","Title":"{{title}}"}""");
}

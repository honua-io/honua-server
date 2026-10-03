// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data.Common;
using System.Diagnostics;
using System.Transactions;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Db.Postgres.Features.Authorization;
using Honua.Db.Postgres.Features.Infrastructure;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Npgsql;
using Xunit;
using IsolationLevel = System.Data.IsolationLevel;

namespace Honua.Db.Postgres.Security.Tests;

public sealed class PostgresCombinedReadPolicyStoreTests(PostgresFixture fixture)
    : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private static readonly string[] ExpectedAttributes = ["*", "ALPHA", "anonymous", "beta"];
    private string _schema = null!;
    private RecordingProvider _provider = null!;
    private PostgresRlsPolicyStore _rows = null!;
    private PostgresFieldMaskPolicyStore _fields = null!;

    public async Task InitializeAsync()
    {
        _schema = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresCombinedReadPolicyStoreTests));
        await fixture.ExecuteAsync($$"""
            CREATE TABLE {{_schema}}.rbac_rls_policies (
                policy_id uuid PRIMARY KEY, role text, service text, layer text, attribute text,
                claim_type text, comparison smallint, description text,
                created_at timestamptz, updated_at timestamptz);
            CREATE TABLE {{_schema}}.rbac_field_mask_policies (
                policy_id uuid PRIMARY KEY, role text, service text, layer text, attribute text,
                description text, created_at timestamptz, updated_at timestamptz);
            """);
        _provider = new RecordingProvider(fixture.DataSource);
        _rows = new PostgresRlsPolicyStore(_provider, _schema);
        _fields = new PostgresFieldMaskPolicyStore(_provider, _schema);
    }

    public Task DisposeAsync() => fixture.DropSchemaAsync(_schema);

    [IntegrationTest]
    [SecurityTest]
    public async Task GetEffectiveReadPoliciesAsync_MultipleScopes_ReturnsFreshPoliciesInOneLeaseAndBatch()
    {
        foreach (var service in new[] { "*", "ALPHA", "beta", "other" })
        {
            await AddAsync(service, "reader", "parcels", service);
        }
        await AddAsync("*", "*", "*", "anonymous");
        await AddAsync("*", "other-role", "parcels", "wrong_role");
        await AddAsync("*", "reader", "other-layer", "wrong_layer");
        var combined = Assert.IsAssignableFrom<ICombinedReadPolicyStore>(_rows);
        _provider.Opens = 0;
        _provider.Releases = 0;
        var commands = new List<string>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Npgsql",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity =>
            {
                if (activity.GetTagItem("db.query.text") is string text && text.Contains(_schema, StringComparison.Ordinal))
                {
                    commands.Add(text);
                }
            }
        };
        ActivitySource.AddActivityListener(listener);

        var policies = await combined.GetEffectiveReadPoliciesAsync(_fields, [" Reader ", "reader"], ["alpha", "ALPHA", "BETA"], "PARCELS");

        Assert.Equal(1, _provider.Opens);
        Assert.Equal(1, _provider.Releases);
        Assert.Single(commands);
        Assert.Contains("rbac_rls_policies", commands[0]);
        Assert.Contains("rbac_field_mask_policies", commands[0]);
        Assert.Equal(ExpectedAttributes, policies.RowPolicies.Select(p => p.Attribute).Order().ToArray());
        Assert.Equal(4, policies.FieldPolicies.Count);
        Assert.Equal(4, policies.RowPolicies.Select(p => p.PolicyId).Distinct().Count());

        await AddAsync("alpha", "reader", "parcels", "new_policy");
        policies = await combined.GetEffectiveReadPoliciesAsync(_fields, ["reader"], ["alpha", "beta"], "parcels");
        Assert.Contains(policies.RowPolicies, p => p.Attribute == "new_policy");
        Assert.Contains(policies.FieldPolicies, p => p.Attribute == "new_policy");
    }

    [IntegrationTest]
    [SecurityTest]
    public async Task GetEffectiveReadPoliciesAsync_AnonymousWithoutPublication_StillReadsWildcards()
    {
        await AddAsync("*", "*", "*", "global");
        await AddAsync("alpha", "*", "parcels", "specific");
        var policies = await Combined().GetEffectiveReadPoliciesAsync(_fields, [], [], "parcels");
        Assert.Equal("global", Assert.Single(policies.RowPolicies).Attribute);
        Assert.Equal("global", Assert.Single(policies.FieldPolicies).Attribute);
    }

    [IntegrationTheory]
    [SecurityTest]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task GetEffectiveReadPoliciesAsync_MissingLegacyTable_PreservesOtherRestrictions(bool missingRows, bool missingFields)
    {
        await AddAsync("*", "*", "*", "restriction");
        if (missingRows) await fixture.ExecuteAsync($"DROP TABLE {_schema}.rbac_rls_policies");
        if (missingFields) await fixture.ExecuteAsync($"DROP TABLE {_schema}.rbac_field_mask_policies");

        var policies = await Combined().GetEffectiveReadPoliciesAsync(_fields, [], ["alpha", "beta"], "parcels");
        Assert.Equal(missingRows ? 0 : 1, policies.RowPolicies.Count);
        Assert.Equal(missingFields ? 0 : 1, policies.FieldPolicies.Count);
        Assert.Equal(_provider.Opens, _provider.Releases);
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("SELECT 1", connection);
        Assert.Equal(1, await command.ExecuteScalarAsync());
    }

    [IntegrationTest]
    [SecurityTest]
    public async Task GetEffectiveReadPoliciesAsync_NonLegacyFailure_PropagatesAndReturnsLease()
    {
        await fixture.ExecuteAsync($"ALTER TABLE {_schema}.rbac_field_mask_policies DROP COLUMN attribute");
        var error = await Assert.ThrowsAsync<PostgresException>(() => Combined().GetEffectiveReadPoliciesAsync(_fields, [], [], "parcels"));
        Assert.Equal(PostgresErrorCodes.UndefinedColumn, error.SqlState);
        Assert.Equal(_provider.Opens, _provider.Releases);
        // A max-one pool makes a leaked connection observable on the next acquisition.
        await using var dataSource = NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(fixture.ConnectionString)
        { MaxPoolSize = 1, Timeout = 2 }.ConnectionString);
        var provider = new RecordingProvider(dataSource);
        var rows = new PostgresRlsPolicyStore(provider, _schema);
        var fields = new PostgresFieldMaskPolicyStore(provider, _schema);
        await Assert.ThrowsAsync<PostgresException>(() => Assert.IsAssignableFrom<ICombinedReadPolicyStore>(rows).GetEffectiveReadPoliciesAsync(fields, [], [], "parcels"));
        await using var returned = await dataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("SELECT 1", returned);
        Assert.Equal(1, await command.ExecuteScalarAsync());
    }

    [IntegrationTest]
    [SecurityTest]
    public async Task GetEffectiveReadPoliciesAsync_Cancelled_DoesNotOpenLease()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Combined().GetEffectiveReadPoliciesAsync(_fields, [], [], "parcels", cancelled.Token));
        Assert.Equal(0, _provider.Opens);
    }

    [IntegrationTest]
    [SecurityTest]
    public async Task GetEffectiveReadPoliciesAsync_CancelledDuringFieldRead_ReturnsOwnedLease()
    {
        await using var blocker = await fixture.DataSource.OpenConnectionAsync();
        await using var transaction = await blocker.BeginTransactionAsync();
        await using var command = new NpgsqlCommand($"LOCK TABLE {_schema}.rbac_field_mask_policies IN ACCESS EXCLUSIVE MODE", blocker, transaction);
        await command.ExecuteNonQueryAsync();
        using var cancelled = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Combined().GetEffectiveReadPoliciesAsync(_fields, [], [], "parcels", cancelled.Token));
        Assert.Equal(1, _provider.Opens);
        Assert.Equal(1, _provider.Releases);
        await transaction.RollbackAsync();
        Assert.Empty((await Combined().GetEffectiveReadPoliciesAsync(_fields, [], [], "parcels")).FieldPolicies);
    }

    [IntegrationTest]
    [SecurityTest]
    public async Task CanResolveWith_DifferentProviderOrSchema_RejectsPair()
    {
        var combined = Combined();
        Assert.False(combined.CanResolveWith(new PostgresFieldMaskPolicyStore(new RecordingProvider(fixture.DataSource), _schema)));
        Assert.False(combined.CanResolveWith(new PostgresFieldMaskPolicyStore(_provider, "other")));
        await Assert.ThrowsAsync<ArgumentException>(() => combined.GetEffectiveReadPoliciesAsync(
            new PostgresFieldMaskPolicyStore(_provider, "other"), [], [], "parcels"));
    }

    [IntegrationTest]
    [SecurityTest]
    public async Task CanResolveWith_CallerTransactions_UsesExistingStandalonePath()
    {
        var combined = Combined();
        using (var ambient = new TransactionScope(TransactionScopeAsyncFlowOption.Enabled))
        {
            Assert.False(combined.CanResolveWith(_fields));
            ambient.Complete();
        }
        await PostgresMutationTransaction.ExecuteAsync(_provider, async () =>
        {
            Assert.False(combined.CanResolveWith(_fields));
            await AddAsync("*", "*", "*", "uncommitted");
            Assert.Single(await _rows.GetEffectivePoliciesAsync([], "alpha", "parcels"));
            Assert.Single(await _fields.GetEffectivePoliciesAsync([], "alpha", "parcels"));
            return true;
        }, _ => false, CancellationToken.None);
        Assert.Empty(await _rows.GetEffectivePoliciesAsync([], "alpha", "parcels"));
        Assert.Empty(await _fields.GetEffectivePoliciesAsync([], "alpha", "parcels"));
    }

    private ICombinedReadPolicyStore Combined() => Assert.IsAssignableFrom<ICombinedReadPolicyStore>(_rows);

    private async Task AddAsync(string service, string role, string layer, string attribute)
    {
        await _rows.CreatePolicyAsync(new RlsPolicy { Service = service, Role = role, Layer = layer, Attribute = attribute, ClaimType = "region" });
        await _fields.CreatePolicyAsync(new FieldMaskPolicy { Service = service, Role = role, Layer = layer, Attribute = attribute });
    }

    private sealed class RecordingProvider(NpgsqlDataSource dataSource) : IAdoNetDatabaseConnectionProvider
    {
        public int Opens { get; set; }
        public int Releases { get; set; }
        public string GetConnectionString() => dataSource.ConnectionString;
        public Task<T> ExecuteWithDeadlockRetryAsync<T>(Func<Task<T>> operation, CancellationToken cancellationToken = default)
            => operation();
        public Task ExecuteWithDeadlockRetryAsync(Func<Task> operation, CancellationToken cancellationToken = default)
            => operation();
        public async Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Opens++;
            return new SemaphoreReleasingConnection(await dataSource.OpenConnectionAsync(cancellationToken), () => Releases++);
        }
        public async Task<(DbConnection Connection, DbTransaction Transaction)> OpenTransactionAsync(
            IsolationLevel isolationLevel = IsolationLevel.RepeatableRead, CancellationToken cancellationToken = default)
        {
            var connection = await OpenConnectionAsync(cancellationToken);
            return (connection, await connection.BeginTransactionAsync(isolationLevel, cancellationToken));
        }
    }
}

// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data.Common;
using System.Reflection;
using FluentAssertions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Events.Outbox;
using Honua.Postgres.Features.FeatureStore.Services;
using Honua.Postgres.Features.Infrastructure.Events.Outbox;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.ObjectPool;
using NSubstitute;

// Honua.Core's IGeometryProcessor is internal; we sidestep Castle DynamicProxy access
// checks by using the concrete Honua.Postgres GeometryProcessor instead of a mock.

namespace Honua.Postgres.Tests.Features.FeatureStore;

public sealed class FeatureDataAccessOutboxScopeWiringTests
{
    [Fact]
    public void TryUseTransactionalOutbox_WithScopeButNoRepository_ThrowsToPreventSilentCdcLoss()
    {
        // Outbox wiring guard (#692): if the protocol layer opens an outbox scope but the
        // data access has no IFeatureChangeOutboxWriter injected, the mutation must not
        // silently fall through to the autocommit fast path — that would commit the feature
        // without recording its CDC envelope, which is the exact gap the transactional
        // outbox is meant to close. The helper must surface the wiring error as a mutation
        // failure so the misconfiguration is visible at the protocol boundary instead of
        // appearing as silent change-data loss to consumers.
        var dataAccess = BuildMinimalDataAccess(outboxRepository: null);
        var scopeData = new FeatureMutationOutboxScopeData
        {
            EntryFactory = (_, _, _) => null
        };

        using var _ = FeatureMutationOutboxScope.Begin(scopeData);

        var act = () => InvokeTryUseTransactionalOutbox(dataAccess);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*outbox scope is active but no IFeatureChangeOutboxWriter*");
    }

    [Fact]
    public void TryUseTransactionalOutbox_WithoutScope_ReturnsFalseEvenWhenRepositoryAbsent()
    {
        // Non-outbox callers (legacy / SQL Server / DuckDB / smoke tests with no scope) must
        // continue to fall through to the autocommit path silently. The throw is reserved
        // for the wiring-error combination — scope opened but repository missing.
        var dataAccess = BuildMinimalDataAccess(outboxRepository: null);

        FeatureMutationOutboxScope.Current.Should().BeNull();

        var (result, scope) = InvokeTryUseTransactionalOutboxWithOut(dataAccess);

        result.Should().BeFalse();
        scope.Should().BeNull();
    }

    [Fact]
    public void TryUseTransactionalOutbox_WithScopeAndRepository_ReturnsTrueAndSurfacesScope()
    {
        // Happy path: when both pieces are wired up the helper returns true and surfaces
        // the active scope so the caller can hand its EntryFactory to the writer.
        var repository = new StubOutboxWriter();
        var dataAccess = BuildMinimalDataAccess(outboxRepository: repository);
        var scopeData = new FeatureMutationOutboxScopeData
        {
            EntryFactory = (_, _, _) => null
        };

        using var _ = FeatureMutationOutboxScope.Begin(scopeData);

        var (result, scope) = InvokeTryUseTransactionalOutboxWithOut(dataAccess);

        result.Should().BeTrue();
        scope.Should().BeSameAs(scopeData);
    }

    private static FeatureDataAccess BuildMinimalDataAccess(IFeatureChangeOutboxWriter? outboxRepository)
    {
        // Use the concrete GeometryProcessor (and a real ObjectPool) to avoid
        // Castle DynamicProxy access checks on the internal IGeometryProcessor — this
        // helper is only constructed to invoke the private TryUseTransactionalOutbox
        // path, which never touches geometry or cache state.
        var dependencies = new FeatureDataAccessDependencies(
            ConnectionProvider: Substitute.For<IDatabaseConnectionProvider>(),
            GeometryProcessor: new GeometryProcessor(),
            CacheManager: Substitute.For<IFeatureCacheManager>(),
            DictionaryPool: new DefaultObjectPoolProvider().Create(new DictionaryPooledObjectPolicy()),
            StatementCache: null,
            Logger: NullLogger<FeatureDataAccess>.Instance,
            PerformanceOptions: null,
            LimitsOptions: null,
            PerformanceMonitor: null,
            SchemaName: null,
            OutboxRepository: outboxRepository);

        return new FeatureDataAccess(dependencies);
    }

    private static void InvokeTryUseTransactionalOutbox(FeatureDataAccess dataAccess)
    {
        var method = typeof(FeatureDataAccess).GetMethod(
            "TryUseTransactionalOutbox",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("TryUseTransactionalOutbox not found.");

        var args = new object?[] { null };
        try
        {
            method.Invoke(dataAccess, args);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            throw ex.InnerException;
        }
    }

    private static (bool Result, FeatureMutationOutboxScopeData? Scope) InvokeTryUseTransactionalOutboxWithOut(
        FeatureDataAccess dataAccess)
    {
        var method = typeof(FeatureDataAccess).GetMethod(
            "TryUseTransactionalOutbox",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("TryUseTransactionalOutbox not found.");

        var args = new object?[] { null };
        bool result;
        try
        {
            result = (bool)method.Invoke(dataAccess, args)!;
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            throw ex.InnerException;
        }

        return (result, (FeatureMutationOutboxScopeData?)args[0]);
    }

    // IFeatureChangeOutboxWriter is internal to Honua.Postgres, which does not grant
    // DynamicProxyGenAssembly2 access, so Castle DynamicProxy cannot mock it. Use a concrete
    // stub — consistent with the GeometryProcessor approach above. The write path is never
    // invoked here; this test only verifies the wiring helper recognizes a present writer.
    private sealed class StubOutboxWriter : IFeatureChangeOutboxWriter
    {
        public Task WriteOutboxRowAsync(
            DbConnection connection,
            DbTransaction transaction,
            FeatureChangeOutboxEntry entry,
            CancellationToken cancellationToken) => Task.CompletedTask;
    }
}

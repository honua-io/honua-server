// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Collaboration.FeatureLocks;
using Honua.Core.Features.Edit;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.Geometry.Abstractions;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Core.Queries.Filters;
using Honua.Infrastructure.Events;
using Honua.Infrastructure.Validation;
using Honua.Plugins.Abstractions;
using Honua.Protocols.GeoServices.FeatureServer;
using Honua.Protocols.GeoServices.FeatureServer.Models;
using Honua.Protocols.GeoServices.FeatureServer.Services;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer;

/// <summary>
/// #6359: once a multi-batch calculate commit is acknowledged, a failed idempotency
/// receipt must not release the reservation. A retry then conflicts instead of
/// applying the same increment again.
/// </summary>
public sealed class FeatureServerCalculateDurableCommitTests
{
    [UnitTest]
    public async Task CommitAmbient_FirstReceiptFailure_KeepsReservationSoRetryCannotApplyAgain()
    {
        var store = new ReceiptFailingStore();
        var transaction = Substitute.For<IFeatureWriterTransaction>();
        transaction.CommitAsync(Arg.Any<CancellationToken>())
            .Returns(FeatureWriterTransactionCommitOutcome.Committed);
        var handler = CreateHandler(store);
        var committedScope = new ApplyEditsIdempotencyScope("svc", 1, "user", "calc-key-0");
        var idleScope = new ApplyEditsIdempotencyScope("svc", 1, "user", "calc-key-idle");
        store.Hold(committedScope.IdempotencyKey);
        store.Hold(idleScope.IdempotencyKey);
        var ambient = new AmbientFeatureEditTransaction { Transaction = transaction };
        ambient.HeldReservations.Add((committedScope, "token-0"));
        ambient.HeldReservations.Add((idleScope, "token-idle"));
        ambient.Recorded.Add((committedScope, "token-0", new ApplyEditsResponse { Success = true }));

        var committed = await handler.CommitAmbientEditTransactionAsync(ambient, CancellationToken.None);

        committed.Should().BeTrue("the provider commit already succeeded");
        ambient.PreserveReservations.Should().BeTrue();
        store.ReleasedTokens.Should().Equal("token-idle");
        (await store.TryReserveAsync(committedScope)).Should().BeNull(
            "the committed batch stays reserved, so a retry cannot win the key and apply again");
        await transaction.DidNotReceive().RollbackAsync(Arg.Any<CancellationToken>());
    }

    [UnitTest]
    public async Task CommitAmbient_PostCommitHookFailure_StillReportsDurableCommit()
    {
        var store = new ReceiptFailingStore { FailReceipts = false };
        var transaction = Substitute.For<IFeatureWriterTransaction>();
        transaction.CommitAsync(Arg.Any<CancellationToken>())
            .Returns(FeatureWriterTransactionCommitOutcome.Committed);
        var handler = CreateHandler(store);
        var scope = new ApplyEditsIdempotencyScope("svc", 1, "user", "calc-key-hook");
        store.Hold(scope.IdempotencyKey);
        var ambient = new AmbientFeatureEditTransaction { Transaction = transaction };
        ambient.HeldReservations.Add((scope, "token-hook"));
        ambient.Recorded.Add((scope, "token-hook", new ApplyEditsResponse { Success = true }));
        ambient.AfterCommit.Add(_ => throw new InvalidOperationException("injected post-commit failure"));

        var committed = await handler.CommitAmbientEditTransactionAsync(ambient, CancellationToken.None);

        committed.Should().BeTrue();
        ambient.PreserveReservations.Should().BeTrue();
        store.ReleasedTokens.Should().BeEmpty();
        store.RecordedTokens.Should().Equal("token-hook");
        await transaction.DidNotReceive().RollbackAsync(Arg.Any<CancellationToken>());
    }

    [UnitTest]
    public async Task CommitAmbient_LostAcknowledgement_DoesNotMarkDurable()
    {
        var store = new ReceiptFailingStore();
        var transaction = Substitute.For<IFeatureWriterTransaction>();
        transaction.CommitAsync(Arg.Any<CancellationToken>())
            .Returns(FeatureWriterTransactionCommitOutcome.Unknown);
        var handler = CreateHandler(store);
        var ambient = new AmbientFeatureEditTransaction { Transaction = transaction };

        var committed = await handler.CommitAmbientEditTransactionAsync(ambient, CancellationToken.None);

        committed.Should().BeFalse();
        ambient.PreserveReservations.Should().BeFalse();
        store.SetAttempts.Should().Be(0);
    }

    private static FeatureServerEditsHandler CreateHandler(IApplyEditsIdempotencyStore store)
        => new(
            new FeatureServerEditsDependencies(
                Substitute.For<IResourceValidator>(),
                Substitute.For<IFeatureWriter>(),
                Substitute.For<IFeatureReader>(),
                Substitute.For<IFeatureServerGeometryServices>(),
                Substitute.For<IEditParameterAdapter<GeoServicesEditRequest>>(),
                Substitute.For<IEditProcessor>(),
                new FeatureMutationValidator(Substitute.For<IGeometryValidator>()),
                Substitute.For<IFilterExpressionService>(),
                Substitute.For<IHttpContextAccessor>(),
                new FeatureMutationEventService(Substitute.For<IFeatureChangeEventPublisher>()),
                Substitute.For<IPluginEditPipeline>(),
                store,
                Substitute.For<IFeatureEditGuard>(),
                Substitute.For<IFeatureLockService>()),
            NullLogger<FeatureServerEditsHandler>.Instance);

    /// <summary>
    /// Keeps reservations until <see cref="ReleaseAsync"/> and fails the first receipt write.
    /// </summary>
    private sealed class ReceiptFailingStore : IApplyEditsIdempotencyStore
    {
        private readonly HashSet<string> _held = new(StringComparer.Ordinal);

        public bool FailReceipts { get; init; } = true;

        public int SetAttempts { get; private set; }

        public List<string> ReleasedTokens { get; } = [];

        public List<string> RecordedTokens { get; } = [];

        public void Hold(string idempotencyKey) => _held.Add(idempotencyKey);

        public Task<ApplyEditsResponse?> TryGetAsync(
            ApplyEditsIdempotencyScope scope,
            CancellationToken cancellationToken = default)
            => Task.FromResult<ApplyEditsResponse?>(null);

        public Task<string?> TryReserveAsync(
            ApplyEditsIdempotencyScope scope,
            CancellationToken cancellationToken = default)
            => Task.FromResult(_held.Contains(scope.IdempotencyKey) ? null : "fresh-token");

        public Task<bool> SetAsync(
            ApplyEditsIdempotencyScope scope,
            string reservationToken,
            ApplyEditsResponse response,
            CancellationToken cancellationToken = default)
        {
            SetAttempts++;
            if (FailReceipts)
            {
                throw new InvalidOperationException("injected idempotency receipt failure");
            }

            _held.Remove(scope.IdempotencyKey);
            RecordedTokens.Add(reservationToken);
            return Task.FromResult(true);
        }

        public Task ReleaseAsync(ApplyEditsIdempotencyScope scope, string reservationToken)
        {
            _held.Remove(scope.IdempotencyKey);
            ReleasedTokens.Add(reservationToken);
            return Task.CompletedTask;
        }
    }
}

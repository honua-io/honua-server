// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Infrastructure.Abstractions;
using StackExchange.Redis;

namespace Honua.Import.FileImport.Resumable;

// One host-owned domain shared by every writer of its configured roots. The
// domain is not registered as ready merely because Redis or its directory exists.
// Native volumes require their own qualified physical census/provisioner binding.
internal sealed class LocalImportStorageDomain : IImportStorageBudget, IStorageDomainObjectMutationFence
{
    private readonly IConnectionMultiplexer _redis;
    private readonly string _domainId;
    private readonly ImportStorageBudgetLimits _limits;
    private readonly LocalImportStorageJournal _journal;
    private readonly LocalImportStorageCensus _census;
    private readonly LocalImportStorageRoots _roots;
    private readonly LocalStorageDomainLock _domainLock;
    private volatile bool _initialized;

    internal LocalImportStorageDomain(IConnectionMultiplexer redis, string domainId,
        ImportStorageBudgetLimits limits, string journalDirectory,
        IReadOnlyDictionary<ImportStorageRootKind, string> roots, int? journalCapacity = null)
    {
        ArgumentNullException.ThrowIfNull(redis);
        // Validate the canonical domain/policy before allocating control files.
        _ = new RedisImportStorageBudget(redis, domainId, limits, Guid.NewGuid().ToString("N"));
        var capacity = journalCapacity ?? limits.MaximumObjects;
        if (capacity < limits.MaximumObjects || capacity > LocalImportStorageCensus.MaximumLocalObjects)
            throw new ArgumentException("This local profile exceeds its bounded physical census capacity.", nameof(limits));
        _redis = redis;
        _domainId = domainId;
        _limits = limits;
        // Physical journal capacity is stable across quota reductions. Existing
        // objects remain accounted even while their usage exceeds a new policy.
        _journal = new(journalDirectory, domainId, capacity);
        _roots = new(roots, _journal.DirectoryPath, domainId);
        _census = new(_journal, roots, capacity, _roots);
        _domainLock = new(journalDirectory);
    }

    internal async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var held = await _domainLock.EnterAsync(exclusive: true, cancellationToken).ConfigureAwait(false);
        _initialized = false;
        string previousJournalEpoch;
        try { previousJournalEpoch = await _journal.ReadEpochAsync(cancellationToken).ConfigureAwait(false); }
        catch (FileNotFoundException)
        {
            _census.VerifyEmpty(cancellationToken);
            _roots.Provision();
            previousJournalEpoch = Guid.NewGuid().ToString("N");
            await _journal.CreateEmptyAsync(previousJournalEpoch, _roots.Fingerprint(), cancellationToken).ConfigureAwait(false);
        }

        var nextEpoch = Guid.NewGuid().ToString("N");
        var binding = await _journal.ReadBindingAsync(cancellationToken).ConfigureAwait(false);
        _roots.RequireFingerprint(binding.RootsSha256);
        var journalId = binding.JournalId;
        if (!await _journal.AdvanceEpochAsync(previousJournalEpoch, nextEpoch, cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("The physical journal changed outside its exclusive domain fence.");
        // Poison the old epoch before census. If census fails, other replicas
        // must not resume writes against an old, apparently healthy Redis ledger.
        var replacement = Bind(nextEpoch, journalId);
        var previousRedisEpoch = await replacement.ReadStoredEpochAsync(cancellationToken).ConfigureAwait(false);
        await _census.VerifyAsync(cancellationToken).ConfigureAwait(false);
        await _journal.RetireAbsentAsync(_census.VerifyDeleted, cancellationToken).ConfigureAwait(false);
        await replacement.RebuildFromCensusAsync(previousRedisEpoch, RetainedAsync(cancellationToken), cancellationToken).ConfigureAwait(false);
        _initialized = true;
    }

    public async ValueTask<IAsyncDisposable> EnterMutationAsync(CancellationToken cancellationToken = default)
    {
        var ready = await EnterReadyAsync(cancellationToken).ConfigureAwait(false);
        return ready.Lease;
    }

    public async Task<bool> TryReserveAsync(ImportStorageReservation reservation, CancellationToken cancellationToken = default)
    {
        _census.RequireBinding(reservation);
        var ready = await EnterReadyAsync(cancellationToken).ConfigureAwait(false);
        await using var held = ready.Lease;
        if (!await _journal.TryRecordIntentAsync(reservation, cancellationToken).ConfigureAwait(false)) return false;
        var recorded = await RequireEntryAsync(reservation.FileId, null, cancellationToken).ConfigureAwait(false);
        await using var objectLease = await ObjectLock(recorded.Slot).EnterAsync(exclusive: true, cancellationToken).ConfigureAwait(false);
        var current = await RequireEntryAsync(reservation.FileId, recorded.Slot, cancellationToken).ConfigureAwait(false);
        if (current.Reservation != reservation || current.State != LocalImportStorageState.Reserved)
            throw new InvalidOperationException("The reservation changed before physical admission.");
        // No physical writer is admitted before this durable intent and Redis
        // charge. An exception (including a lost reply) leaves the intent retained.
        if (await ready.Budget.TryReserveAsync(reservation, cancellationToken).ConfigureAwait(false)) return true;
        if (!await _journal.RetireDeletedAsync(reservation, cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("A denied reservation unexpectedly entered a physical mutation lifetime.");
        return false;
    }

    public async ValueTask<IAsyncDisposable> EnterObjectMutationAsync(string objectKey, long? maximumBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(objectKey);
        var ready = await EnterReadyAsync(cancellationToken).ConfigureAwait(false);
        IAsyncDisposable? objectLease = null;
        try
        {
            var recorded = await RequireEntryAsync(objectKey, null, cancellationToken).ConfigureAwait(false);
            objectLease = await ObjectLock(recorded.Slot).EnterAsync(exclusive: true, cancellationToken).ConfigureAwait(false);
            var entry = await RequireEntryAsync(objectKey, recorded.Slot, cancellationToken).ConfigureAwait(false);
            var reservation = entry.Reservation;
            _census.RequireBinding(reservation);
            if (maximumBytes is { } bytes)
            {
                if (entry.State != LocalImportStorageState.Reserved || reservation.PhysicalLocation!.MaximumContentBytes != bytes ||
                    !await ready.Budget.TryReserveAsync(reservation, cancellationToken).ConfigureAwait(false) ||
                    !await _journal.MarkWriteStartedAsync(reservation, cancellationToken).ConfigureAwait(false))
                    throw new InvalidOperationException("The immutable physical write is not admitted by its original reservation.");
            }
            else if (!await _journal.MarkDeletionStartedAsync(reservation, cancellationToken).ConfigureAwait(false))
                throw new InvalidOperationException("The immutable object is not admitted for deletion.");
            return new MutationLease(objectLease, ready.Lease);
        }
        catch
        {
            try { if (objectLease is not null) await objectLease.DisposeAsync().ConfigureAwait(false); }
            finally { await ready.Lease.DisposeAsync().ConfigureAwait(false); }
            throw;
        }
    }

    public async Task<bool> ReleaseDeletedAsync(ImportStorageReservation reservation, CancellationToken cancellationToken = default)
    {
        _census.RequireBinding(reservation);
        var ready = await EnterReadyAsync(cancellationToken).ConfigureAwait(false);
        await using var held = ready.Lease;
        var recorded = await _journal.ReadEntryAsync(reservation.FileId, cancellationToken).ConfigureAwait(false);
        if (recorded is null) return false;
        await using var objectLease = await ObjectLock(recorded.Slot).EnterAsync(exclusive: true, cancellationToken).ConfigureAwait(false);
        var entry = await RequireEntryAsync(reservation.FileId, recorded.Slot, cancellationToken).ConfigureAwait(false);
        if (entry.Reservation != reservation)
            throw new InvalidOperationException("Deletion does not match the immutable physical reservation.");
        if (entry.State == LocalImportStorageState.Retired) return false;
        if (!_census.VerifyDeleted(reservation))
            throw new IOException("The reservation still owns physical content or metadata.");
        if (!await _journal.MarkDeletionStartedAsync(reservation, cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("The physical reservation could not enter deletion.");
        // false is an idempotent release after an earlier lost Redis response.
        // The exact journal binding and durable physical absence were rechecked.
        _ = await ready.Budget.ReleaseDeletedAsync(reservation, cancellationToken).ConfigureAwait(false);
        if (!await _journal.RetireDeletedAsync(reservation, cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("The physically deleted reservation could not be retired.");
        return true;
    }

    private async Task<(IAsyncDisposable Lease, RedisImportStorageBudget Budget)> EnterReadyAsync(CancellationToken cancellationToken)
    {
        var held = await _domainLock.EnterAsync(exclusive: false, cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_initialized) throw new InvalidOperationException("The local storage domain has not completed physical recovery.");
            var binding = await _journal.ReadBindingAsync(cancellationToken).ConfigureAwait(false);
            _roots.RequireFingerprint(binding.RootsSha256);
            var (epoch, journalId) = (binding.Epoch, binding.JournalId);
            var budget = Bind(epoch, journalId);
            if (await budget.ReadStoredEpochAsync(cancellationToken).ConfigureAwait(false) != epoch)
                throw new InvalidDataException("Storage accounting requires exclusive physical recovery.");
            return (held, budget);
        }
        catch
        {
            await held.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private RedisImportStorageBudget Bind(string epoch, string journalId) => new(_redis, _domainId, _limits, epoch, journalId);

    // One lock per bounded journal slot, not a hash stripe shared by unrelated
    // live objects. Source scratch can remain open while another object is
    // admitted, so a hash collision must never deadlock that nested workflow.
    // Retirement occurs under this lock; slot reuse revalidates its identity.
    private LocalStorageDomainLock ObjectLock(int slot)
        => new(_journal.DirectoryPath, "object-" + slot.ToString(CultureInfo.InvariantCulture) + ".lock");

    private async Task<LocalImportStorageJournalEntry> RequireEntryAsync(string fileId, int? slot, CancellationToken cancellationToken)
    {
        var entry = await _journal.ReadEntryAsync(fileId, cancellationToken).ConfigureAwait(false);
        if (entry is null || slot is { } expected && entry.Slot != expected)
            throw new InvalidOperationException("No matching durable journal slot authorizes this physical object.");
        return entry;
    }

    private async IAsyncEnumerable<ImportStorageReservation> RetainedAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var entry in _journal.ReadRetainedAsync(cancellationToken).ConfigureAwait(false))
            yield return entry.Reservation;
    }

    private sealed class MutationLease(IAsyncDisposable inner, IAsyncDisposable outer) : IAsyncDisposable
    {
        private int _disposed;
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try { await inner.DisposeAsync().ConfigureAwait(false); }
            finally { await outer.DisposeAsync().ConfigureAwait(false); }
        }
    }
}

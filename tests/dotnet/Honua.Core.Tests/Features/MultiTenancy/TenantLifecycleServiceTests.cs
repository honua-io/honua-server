// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using FluentAssertions;
using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Core.Features.MultiTenancy.Domain;
using Honua.Core.Features.MultiTenancy.Lifecycle;

namespace Honua.Core.Tests.Features.MultiTenancy;

/// <summary>
/// Unit tests for the tenant lifecycle state machine and its audit emission (issue #2156).
/// </summary>
public sealed class TenantLifecycleServiceTests
{
    private static readonly TenantLifecycleActor Actor =
        new("operator-1", AuditActorType.UserId, "corr-1");

    [Fact]
    public async Task FullLifecycle_CreateSuspendResumeDelete_TransitionsAndAudits()
    {
        var audit = new CapturingAuditLog();
        var service = new TenantLifecycleService(new InMemoryCatalog(), audit);

        var created = await service.CreateAsync("acme", "Acme Inc", "pro", Actor);
        created.Outcome.Should().Be(TenantLifecycleOutcome.Created);
        created.Tenant!.Status.Should().Be(TenantStatus.Active);
        created.Tenant.Plan.Should().Be("pro");

        var suspended = await service.SuspendAsync("acme", Actor);
        suspended.Outcome.Should().Be(TenantLifecycleOutcome.Updated);
        suspended.Tenant!.Status.Should().Be(TenantStatus.Suspended);
        suspended.Tenant.SuspendedAt.Should().NotBeNull();

        var resumed = await service.ResumeAsync("acme", Actor);
        resumed.Outcome.Should().Be(TenantLifecycleOutcome.Updated);
        resumed.Tenant!.Status.Should().Be(TenantStatus.Active);
        resumed.Tenant.SuspendedAt.Should().BeNull();

        var deleted = await service.DeleteAsync("acme", Actor);
        deleted.Outcome.Should().Be(TenantLifecycleOutcome.Updated);
        deleted.Tenant!.Status.Should().Be(TenantStatus.Deleted);

        audit.Events.Select(e => e.Action).Should()
            .ContainInOrder("tenant.create", "tenant.suspend", "tenant.resume", "tenant.delete");
        audit.Events.Should().OnlyContain(e => e.ResourceType == "tenant" && e.ResourceId == "acme");
    }

    [Fact]
    public async Task Create_DuplicateTenant_ReturnsConflictAndDoesNotAudit()
    {
        var audit = new CapturingAuditLog();
        var service = new TenantLifecycleService(new InMemoryCatalog(), audit);

        await service.CreateAsync("acme", "Acme", null, Actor);
        var second = await service.CreateAsync("acme", "Acme Again", null, Actor);

        second.Outcome.Should().Be(TenantLifecycleOutcome.Conflict);
        audit.Events.Count(e => e.Action == "tenant.create").Should().Be(1);
    }

    [Fact]
    public async Task Suspend_NonActiveTenant_ReturnsInvalidTransition()
    {
        var service = new TenantLifecycleService(new InMemoryCatalog(), new CapturingAuditLog());
        await service.CreateAsync("acme", "Acme", null, Actor);
        await service.SuspendAsync("acme", Actor);

        var again = await service.SuspendAsync("acme", Actor);

        again.Outcome.Should().Be(TenantLifecycleOutcome.InvalidTransition);
    }

    [Fact]
    public async Task Resume_ActiveTenant_ReturnsInvalidTransition()
    {
        var service = new TenantLifecycleService(new InMemoryCatalog(), new CapturingAuditLog());
        await service.CreateAsync("acme", "Acme", null, Actor);

        var resume = await service.ResumeAsync("acme", Actor);

        resume.Outcome.Should().Be(TenantLifecycleOutcome.InvalidTransition);
    }

    [Fact]
    public async Task Operations_OnUnknownTenant_ReturnNotFound()
    {
        var service = new TenantLifecycleService(new InMemoryCatalog(), new CapturingAuditLog());

        (await service.SuspendAsync("ghost", Actor)).Outcome.Should().Be(TenantLifecycleOutcome.NotFound);
        (await service.DeleteAsync("ghost", Actor)).Outcome.Should().Be(TenantLifecycleOutcome.NotFound);
    }

    [Fact]
    public async Task Create_InvalidTenantId_ReturnsInvalid()
    {
        var service = new TenantLifecycleService(new InMemoryCatalog(), new CapturingAuditLog());

        (await service.CreateAsync("   ", "blank", null, Actor)).Outcome
            .Should().Be(TenantLifecycleOutcome.Invalid);
    }

    private sealed class InMemoryCatalog : ITenantCatalog
    {
        private readonly ConcurrentDictionary<string, TenantRecord> _tenants =
            new(StringComparer.OrdinalIgnoreCase);

        public Task<TenantRecord?> GetAsync(string tenantId, CancellationToken cancellationToken = default)
        {
            _tenants.TryGetValue(tenantId, out var record);
            return Task.FromResult(record);
        }

        public Task<IReadOnlyList<TenantRecord>> ListAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<TenantRecord>>(_tenants.Values.ToList());

        public Task<bool> TryAddAsync(TenantRecord tenant, CancellationToken cancellationToken = default)
            => Task.FromResult(_tenants.TryAdd(tenant.TenantId, tenant));

        public Task<TenantRecord?> UpdateAsync(TenantRecord tenant, CancellationToken cancellationToken = default)
        {
            if (!_tenants.ContainsKey(tenant.TenantId))
            {
                return Task.FromResult<TenantRecord?>(null);
            }

            _tenants[tenant.TenantId] = tenant;
            return Task.FromResult<TenantRecord?>(tenant);
        }
    }

    private sealed class CapturingAuditLog : IAuditLog
    {
        public List<AuditEvent> Events { get; } = [];

        public Task RecordAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
        {
            Events.Add(auditEvent);
            return Task.CompletedTask;
        }
    }
}

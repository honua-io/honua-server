// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Security.Claims;
using FluentAssertions;
using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Infrastructure.Middleware;
using Microsoft.AspNetCore.Http;

namespace Honua.Server.Tests.Infrastructure.Middleware;

/// <summary>
/// Unit tests for <see cref="AuditingFeatureWriter"/> — the shared edit-pipeline
/// decorator that emits destructive-write audit events for every protocol
/// adapter (#507).
/// </summary>
public sealed class AuditingFeatureWriterTests
{
    private readonly CapturingAuditLog _auditLog = new();
    private readonly StubFeatureWriter _inner = new();
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly AuditingFeatureWriter _writer;

    public AuditingFeatureWriterTests()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.Name, "operator@example.com") },
            authenticationType: "Test"));
        httpContext.Connection.RemoteIpAddress = System.Net.IPAddress.Loopback;
        _httpContextAccessor = new HttpContextAccessor { HttpContext = httpContext };
        _writer = new AuditingFeatureWriter(_inner, _auditLog, _httpContextAccessor);
    }

    [Fact]
    public async Task DeleteAsync_WhenDeleted_EmitsFeatureDeleteSuccess()
    {
        _inner.DeleteResult = true;

        var result = await _writer.DeleteAsync(layerId: 7, featureId: 42);

        result.Should().BeTrue();
        var evt = _auditLog.Events.Should().ContainSingle().Subject;
        evt.EventType.Should().Be(AuditEventType.DataDelete);
        evt.Action.Should().Be("feature.delete");
        evt.Outcome.Should().Be(AuditOutcome.Success);
        evt.ResourceType.Should().Be("feature");
        evt.ResourceId.Should().Be("7");
        evt.Actor.Should().Be("operator@example.com");
        evt.ActorType.Should().Be(AuditActorType.UserId);
        evt.Details.Should().Contain("\"objectId\":42");
        evt.Details.Should().NotContain("connection");
    }

    [Fact]
    public async Task DeleteAsync_WhenNotFound_EmitsFailureOutcome()
    {
        _inner.DeleteResult = false;

        await _writer.DeleteAsync(layerId: 3, featureId: 99);

        var evt = _auditLog.Events.Should().ContainSingle().Subject;
        evt.Action.Should().Be("feature.delete");
        evt.Outcome.Should().Be(AuditOutcome.Failure);
    }

    [Fact]
    public async Task DeleteAsync_WhenInnerThrows_EmitsFailureAndRethrows()
    {
        _inner.ThrowOnDelete = true;

        var act = async () => await _writer.DeleteAsync(layerId: 1, featureId: 5);

        await act.Should().ThrowAsync<InvalidOperationException>();
        var evt = _auditLog.Events.Should().ContainSingle().Subject;
        evt.Outcome.Should().Be(AuditOutcome.Failure);
    }

    [Fact]
    public async Task ApplyEditsAsync_WithDeletes_EmitsBulkDeleteEvent()
    {
        _inner.EditResult = FeatureEditResult.Success(createdCount: 0, updatedCount: 0, deletedCount: 2);
        var batch = FeatureEditBatch.Create(deletes: ImmutableArray.Create(1L, 2L));

        await _writer.ApplyEditsAsync(layerId: 11, batch);

        var evt = _auditLog.Events.Should().ContainSingle().Subject;
        evt.EventType.Should().Be(AuditEventType.DataDelete);
        evt.Action.Should().Be("feature.bulk-edit.delete");
        evt.Outcome.Should().Be(AuditOutcome.Success);
        evt.Details.Should().Contain("\"deleted\":2");
    }

    [Fact]
    public async Task ApplyEditsAsync_WithMultipleCreatesNoDelete_EmitsBulkEdit()
    {
        _inner.EditResult = FeatureEditResult.Success(createdCount: 3, updatedCount: 0, deletedCount: 0);
        var creates = ImmutableArray.Create(NewFeature(), NewFeature(), NewFeature());
        var batch = FeatureEditBatch.Create(creates: creates);

        await _writer.ApplyEditsAsync(layerId: 4, batch);

        var evt = _auditLog.Events.Should().ContainSingle().Subject;
        evt.Action.Should().Be("feature.bulk-edit");
        evt.Outcome.Should().Be(AuditOutcome.Success);
    }

    [Fact]
    public async Task ApplyEditsAsync_SingleCreate_DoesNotEmit()
    {
        _inner.EditResult = FeatureEditResult.Success(createdCount: 1, updatedCount: 0, deletedCount: 0);
        var batch = FeatureEditBatch.Create(creates: ImmutableArray.Create(NewFeature()));

        await _writer.ApplyEditsAsync(layerId: 4, batch);

        _auditLog.Events.Should().BeEmpty();
    }

    [Fact]
    public async Task CreateAsync_And_UpdateAsync_AreNotAudited()
    {
        await _writer.CreateAsync(2, NewFeature());
        await _writer.UpdateAsync(2, NewFeature());

        _auditLog.Events.Should().BeEmpty();
    }

    private static Feature NewFeature() => new()
    {
        Id = 0,
        Geometry = null,
        Attributes = ImmutableDictionary<string, object?>.Empty,
    };

    private sealed class StubFeatureWriter : IFeatureWriter
    {
        public bool DeleteResult { get; set; } = true;

        public bool ThrowOnDelete { get; set; }

        public FeatureEditResult EditResult { get; set; } =
            FeatureEditResult.Success(0, 0, 0);

        public Task<Feature> CreateAsync(int layerId, Feature feature, CancellationToken cancellationToken = default)
            => Task.FromResult(feature);

        public Task<Feature> UpdateAsync(int layerId, Feature feature, CancellationToken cancellationToken = default)
            => Task.FromResult(feature);

        public Task<bool> DeleteAsync(int layerId, long featureId, CancellationToken cancellationToken = default)
        {
            if (ThrowOnDelete)
            {
                throw new InvalidOperationException("delete failed");
            }

            return Task.FromResult(DeleteResult);
        }

        public Task<FeatureEditResult> ApplyEditsAsync(
            int layerId,
            FeatureEditBatch editBatch,
            CancellationToken cancellationToken = default)
            => Task.FromResult(EditResult);
    }

    private sealed class CapturingAuditLog : IAuditLog
    {
        public List<AuditEvent> Events { get; } = new();

        public Task RecordAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
        {
            Events.Add(auditEvent);
            return Task.CompletedTask;
        }
    }
}

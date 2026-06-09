// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Honua.Core.Features.AuditLog.Abstractions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Server.Tests.Infrastructure.Middleware;

/// <summary>
/// End-to-end integration tests proving the audit middleware emits events for
/// admin mutations, authentication, and authorization outcomes purely from route
/// metadata, without per-endpoint audit calls (#507).
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Configuration)]
public sealed class AuditLogMiddlewareTests : IAsyncLifetime
{
    private readonly CapturingAuditLog _audit = new();
    private readonly WebAppFixture _fixture;

    public AuditLogMiddlewareTests()
    {
        _fixture = new WebAppFixture().ConfigureServices(services =>
        {
            services.RemoveAll<IAuditLog>();
            services.AddSingleton<IAuditLog>(_audit);
        });
    }

    public Task InitializeAsync() => _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/cache/invalidate")]
    public async Task AdminMutation_Authenticated_EmitsAdminActionEvent()
    {
        var client = _fixture.CreateAdminClient();

        var response = await client.PostAsJsonAsync(
            "/api/v1/admin/cache/invalidate",
            new { scope = "all" });

        // The middleware audits regardless of the handler's success/failure body.
        var evt = await WaitForEventAsync(e => e.EventType == AuditEventType.AdminAction);
        evt.Should().NotBeNull();
        evt!.Action.Should().Be("admin.post");
        evt.ResourceType.Should().Be("admin");
        evt.ResourceId.Should().Be("/api/v1/admin/cache/invalidate");
        evt.CorrelationId.Should().NotBeNullOrWhiteSpace();
        response.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized);
    }

    private async Task<AuditEvent?> WaitForEventAsync(Func<AuditEvent, bool> predicate)
    {
        // The middleware records after the response is written; the audit write is
        // awaited within the request, so a short poll covers test-host scheduling.
        for (var attempt = 0; attempt < 50; attempt++)
        {
            var match = _audit.Events.FirstOrDefault(predicate);
            if (match is not null)
            {
                return match;
            }

            await Task.Delay(20);
        }

        return _audit.Events.FirstOrDefault(predicate);
    }

    private sealed class CapturingAuditLog : IAuditLog
    {
        private readonly ConcurrentQueue<AuditEvent> _events = new();

        public IReadOnlyCollection<AuditEvent> Events => _events.ToArray();

        public Task RecordAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
        {
            _events.Enqueue(auditEvent);
            return Task.CompletedTask;
        }
    }
}

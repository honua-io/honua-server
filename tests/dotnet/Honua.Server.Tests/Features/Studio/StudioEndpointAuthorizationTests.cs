// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Core.Features.Studio.Abstractions;
using Honua.Infrastructure.Middleware;
using Honua.Infrastructure.Security;
using Microsoft.AspNetCore.Http;
using Moq;

namespace Honua.Server.Tests.Features.Studio;

/// <summary>
/// Verifies that Studio endpoint denials coordinate their explicit audit write with the
/// request-level generic-audit suppression marker.
/// </summary>
public sealed class StudioEndpointAuthorizationTests
{
    [Fact]
    public async Task DenyAsync_AuditWriteSucceeds_MarksAuthorizationFailureAudited()
    {
        var auditLog = new Mock<IAuditLog>(MockBehavior.Strict);
        auditLog
            .Setup(log => log.RecordAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var authorization = CreateAuthorization(auditLog.Object);
        var context = new DefaultHttpContext();

        await authorization.DenyAsync(
            context,
            StudioAuthorizationOperation.ReadContentItem,
            "studio-content-item",
            Guid.NewGuid().ToString("D"),
            "studio_authorization/cross_user_denied",
            "The caller does not own this Studio resource.");

        AuditContextResolver.IsAuthorizationFailureAudited(context).Should().BeTrue();
        auditLog.Verify(
            log => log.RecordAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task DenyAsync_AuditWriteThrows_DoesNotMarkAuthorizationFailureAudited()
    {
        var auditLog = new Mock<IAuditLog>(MockBehavior.Strict);
        auditLog
            .Setup(log => log.RecordAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>()))
            .Returns(Task.FromException(new InvalidOperationException("audit sink failed")));
        var authorization = CreateAuthorization(auditLog.Object);
        var context = new DefaultHttpContext();

        Func<Task> act = () => authorization.DenyAsync(
            context,
            StudioAuthorizationOperation.ReadContentItem,
            "studio-content-item",
            Guid.NewGuid().ToString("D"),
            "studio_authorization/cross_user_denied",
            "The caller does not own this Studio resource.");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("audit sink failed");
        AuditContextResolver.IsAuthorizationFailureAudited(context).Should().BeFalse(
            "a failed explicit write must not mark the request as successfully audited");
    }

    private static StudioEndpointAuthorization CreateAuthorization(IAuditLog auditLog)
    {
        var authorizationService = new Mock<IStudioAuthorizationService>(MockBehavior.Strict);
        return new StudioEndpointAuthorization(
            authorizationService.Object,
            auditLog,
            TimeProvider.System);
    }
}

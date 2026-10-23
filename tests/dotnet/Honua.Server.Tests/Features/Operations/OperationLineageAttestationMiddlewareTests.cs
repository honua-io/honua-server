// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using System.Net;
using System.Security.Claims;
using Honua.Core.Features.Operations.Domain;
using Honua.Server.Features.Operations;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Http;

namespace Honua.Server.Tests.Features.OperationLineage;

public sealed class OperationLineageAttestationMiddlewareTests
{
    [UnitTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvokeAsync_LoopbackAdmission_ExpiresAndCannotReplay(bool expired)
    {
        var clock = new AdmissionClock();
        var store = new OperationLineageAttestationStore(clock);
        var id = Guid.NewGuid();
        var admission = new OperationLoopbackAdmission(id, "POST", "/api/v1/admin/import/upload-url", "tenant-a");
        var token = store.Issue(new OperationPolicyContext { OperationInstanceId = "opinst-a" }, admission);
        if (expired) clock.Now = clock.Now.AddMinutes(6);
        var middleware = new OperationLineageAttestationMiddleware(_ => Task.CompletedTask);

        var first = LoopbackContext(id, token);
        await middleware.InvokeAsync(first, store);
        first.Features.Get<OperationLoopbackAdmission>().Should().Be(expired ? null : admission);

        var replay = LoopbackContext(id, token);
        await middleware.InvokeAsync(replay, store);
        replay.Features.Get<OperationLoopbackAdmission>().Should().BeNull();
    }

    private static DefaultHttpContext LoopbackContext(Guid credential, string token)
    {
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("api_key_id", credential.ToString())], "ApiKey"))
        };
        context.Connection.RemoteIpAddress = IPAddress.Loopback;
        context.Request.Method = "POST";
        context.Request.Path = "/api/v1/admin/import/upload-url";
        context.Request.Headers[OperationLineageHeaders.Attestation] = token;
        return context;
    }

    private sealed class AdmissionClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [UnitTest]
    public async Task InvokeAsync_UnattestedPublicHeaders_AreStripped()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers[OperationLineageHeaders.OperationInstanceId] = "spoofed-operation";
        context.Request.Headers[OperationLineageHeaders.AuditId] = "spoofed-audit";
        context.Request.Headers[OperationLineageHeaders.ProposalId] = "spoofed-proposal";
        var middleware = new OperationLineageAttestationMiddleware(nextContext =>
        {
            nextContext.Request.Headers.ContainsKey(OperationLineageHeaders.OperationInstanceId).Should().BeFalse();
            nextContext.Request.Headers.ContainsKey(OperationLineageHeaders.AuditId).Should().BeFalse();
            nextContext.Request.Headers.ContainsKey(OperationLineageHeaders.ProposalId).Should().BeFalse();
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(context, new OperationLineageAttestationStore(TimeProvider.System));
    }

    [UnitTest]
    public async Task InvokeAsync_OneUseAttestation_RestoresExactCanonicalHeaders()
    {
        var store = new OperationLineageAttestationStore(TimeProvider.System);
        var policy = new OperationPolicyContext
        {
            OperationInstanceId = "opinst-exact",
            AuditId = "audit-exact",
            ProposalId = "proposal-exact",
        };
        using var message = new HttpRequestMessage();
        OperationLineageHeaders.Apply(message, policy, store);
        var context = new DefaultHttpContext();
        foreach (var header in message.Headers)
        {
            context.Request.Headers[header.Key] = header.Value.ToArray();
        }

        var middleware = new OperationLineageAttestationMiddleware(nextContext =>
        {
            nextContext.Request.Headers[OperationLineageHeaders.OperationInstanceId].ToString()
                .Should().Be("opinst-exact");
            nextContext.Request.Headers[OperationLineageHeaders.AuditId].ToString()
                .Should().Be("audit-exact");
            nextContext.Request.Headers[OperationLineageHeaders.ProposalId].ToString()
                .Should().Be("proposal-exact");
            nextContext.Request.Headers.ContainsKey(OperationLineageHeaders.Attestation).Should().BeFalse();
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(context, store);

        var replay = new DefaultHttpContext();
        replay.Request.Headers[OperationLineageHeaders.Attestation] =
            message.Headers.GetValues(OperationLineageHeaders.Attestation).Single();
        var replayMiddleware = new OperationLineageAttestationMiddleware(nextContext =>
        {
            nextContext.Request.Headers.ContainsKey(OperationLineageHeaders.OperationInstanceId).Should().BeFalse();
            nextContext.Request.Headers.ContainsKey(OperationLineageHeaders.AuditId).Should().BeFalse();
            nextContext.Request.Headers.ContainsKey(OperationLineageHeaders.ProposalId).Should().BeFalse();
            return Task.CompletedTask;
        });
        await replayMiddleware.InvokeAsync(replay, store);
    }
}

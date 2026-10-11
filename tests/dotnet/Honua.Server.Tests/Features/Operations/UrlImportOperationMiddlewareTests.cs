// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;
using Honua.Core.Features.Operations.Services;
using Honua.Import.FileImport;
using Honua.Infrastructure.Security;
using Honua.Server.Features.Operations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Honua.Server.Tests.Features.OperationsToolset;

[Trait("Tier", "Fast")]
public sealed class UrlImportOperationMiddlewareTests
{
    private const string Body = """{"sourceUrl":"https://example.s3.amazonaws.com/fixture.geojson","tableName":"fixture","forceBackground":true}""";

    [Fact]
    public async Task InvokeAsync_UnkeyedRequest_PreservesRawHandlerWithoutResolvingOperations()
    {
        var called = false;
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.SetEndpoint(new Endpoint(_ => Task.CompletedTask, new EndpointMetadataCollection(new UrlImportOperationMetadata()), "URL import"));
        await new UrlImportOperationMiddleware(_ => { called = true; return Task.CompletedTask; }).InvokeAsync(context);
        Assert.True(called);
    }

    [Theory]
    [InlineData("/api/v1/admin/import/upload-url")]
    [InlineData("/api/v1/admin/imports")]
    public async Task InvokeAsync_EquivalentRetry_ReplaysOneCanonicalJob(string path)
    {
        using var fixture = new Fixture();
        var first = fixture.Context(path);
        var retry = fixture.Context(path);
        await fixture.InvokeAsync(first);
        await fixture.InvokeAsync(retry);

        Assert.Equal(202, first.Response.StatusCode);
        Assert.Equal(202, retry.Response.StatusCode);
        Assert.Equal(ReadBody(first), ReadBody(retry));
        Assert.Equal(1, fixture.Actuations);
        Assert.Equal(first.Response.Headers["X-Honua-Operation-Instance-Id"], retry.Response.Headers["X-Honua-Operation-Instance-Id"]);
    }

    [Fact]
    public async Task InvokeAsync_DefaultsAndPropertyOrder_ReplayTheSameEffectiveRequest()
    {
        using var fixture = new Fixture();
        var first = fixture.Context();
        var retry = fixture.Context(body: """{"forceBackground":true,"targetSrid":4326,"overwriteExisting":false,"trackProgress":false,"tableName":"fixture","sourceUrl":"https://example.s3.amazonaws.com/fixture.geojson"}""");
        await fixture.InvokeAsync(first);
        await fixture.InvokeAsync(retry);
        Assert.Equal(ReadBody(first), ReadBody(retry));
        Assert.Equal(1, fixture.Actuations);
    }

    [Fact]
    public async Task InvokeAsync_ChangedPayload_ConflictsWithoutAnotherActuation()
    {
        using var fixture = new Fixture();
        await fixture.InvokeAsync(fixture.Context());
        var changed = fixture.Context(body: Body.Replace("\"fixture\"", "\"other\"", StringComparison.Ordinal));
        await fixture.InvokeAsync(changed);

        Assert.Equal(409, changed.Response.StatusCode);
        Assert.Equal(1, fixture.Actuations);
    }

    [Fact]
    public async Task InvokeAsync_DifferentActorAndTenant_DoNotReplayAnotherCallersJob()
    {
        using var fixture = new Fixture();
        var first = fixture.Context();
        await fixture.InvokeAsync(first);
        var otherActor = fixture.Context(actor: "operator-b");
        await fixture.InvokeAsync(otherActor);
        fixture.Tenant.TenantId.Returns("tenant-b");
        var otherTenant = fixture.Context();
        await fixture.InvokeAsync(otherTenant);

        Assert.Equal(3, fixture.Actuations);
        Assert.NotEqual(ReadBody(first), ReadBody(otherActor));
        Assert.NotEqual(ReadBody(first), ReadBody(otherTenant));
    }

    [Fact]
    public async Task InvokeAsync_KeyComponentDelimiters_DoNotAlias()
    {
        using var fixture = new Fixture();
        var first = fixture.Context();
        var actorId = CanonicalSecurityActor.Resolve(first.User)!.ActorId;
        first.Request.Headers["Idempotency-Key"] = actorId + ":key";
        await fixture.InvokeAsync(first);
        // These tuples would be identical under tenant + ':' + actor + ':' + key.
        fixture.Tenant.TenantId.Returns("tenant-a:" + actorId);
        var second = fixture.Context(key: "key");
        await fixture.InvokeAsync(second);
        Assert.Equal(2, fixture.Actuations);
        Assert.NotEqual(ReadBody(first), ReadBody(second));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("bad\nkey")]
    public async Task InvokeAsync_InvalidKey_NeverActuates(string key)
    {
        using var fixture = new Fixture();
        var context = fixture.Context(key: key);
        await fixture.InvokeAsync(context);
        Assert.Equal(400, context.Response.StatusCode);
        Assert.Equal(0, fixture.Actuations);
    }

    [Theory]
    [InlineData("{\"sourceUrl\":\"\",\"tableName\":\"fixture\"}")]
    [InlineData("{\"sourceUrl\":\"https://example.s3.amazonaws.com/fixture.geojson\",\"tableName\":\" \"}")]
    public async Task InvokeAsync_EmptyRequiredInput_PreservesBadRequest(string body)
    {
        using var fixture = new Fixture();
        var context = fixture.Context(body: body);
        await fixture.InvokeAsync(context);
        Assert.Equal(400, context.Response.StatusCode);
        Assert.Equal(0, fixture.Actuations);
    }

    [Fact]
    public async Task InvokeAsync_ReadScopedAdmin_IsDeniedBeforeReplay()
    {
        using var fixture = new Fixture();
        await fixture.InvokeAsync(fixture.Context());
        var narrowed = fixture.Context(scope: "honua.mcp.read");
        await fixture.InvokeAsync(narrowed);

        Assert.Equal(403, narrowed.Response.StatusCode);
        Assert.Equal(1, fixture.Actuations);
    }

    [Fact]
    public async Task ApprovedReplay_PreservesOriginalRequestBindingWithoutAdapterContext()
    {
        using var fixture = new Fixture();
        var factory = new OperationEnvelopeFactory(fixture.Store, new VolatileOperationAuditLog(), TimeProvider.System);
        var originalContext = new OperationPolicyContext
        {
            PrincipalId = "operator-a", TenantId = "tenant-a", IdempotencyKey = "trusted-actor-scoped-key", IdempotencyRequestHash = new string('a', 64),
        };
        var accepted = await factory.CreateAcceptedAsync("admin.import.upload-url", originalContext);
        await fixture.Store.SetAsync(accepted with { Status = OperationHandleStatus.RequiresApproval });
        var replayed = await fixture.ReplayAsync(accepted);
        Assert.Equal(OperationHandleStatus.Queued, replayed.Status);
        var retry = await factory.CreateAcceptedAsync("admin.import.upload-url", originalContext);
        Assert.Equal(OperationHandleStatus.Queued, retry.Status);
        Assert.Equal(replayed.JobId, retry.JobId);
        Assert.Equal(1, fixture.Actuations);
    }

    [Fact]
    public async Task InvokeAsync_RetryAuditUnavailable_DoesNotReturnStoredSuccess()
    {
        using var fixture = new Fixture();
        var first = fixture.Context();
        await fixture.InvokeAsync(first);
        fixture.AuditAvailable = false;
        var retry = fixture.Context();
        await fixture.InvokeAsync(retry);
        Assert.Equal(503, retry.Response.StatusCode);
        Assert.DoesNotContain("job-1", ReadBody(retry), StringComparison.Ordinal);
        Assert.False(retry.Response.Headers.ContainsKey("X-Honua-Audit-Id"));
        Assert.Equal(1, fixture.Actuations);
        fixture.AuditAvailable = true;
        var recovered = fixture.Context();
        await fixture.InvokeAsync(recovered);
        Assert.Equal(ReadBody(first), ReadBody(recovered));
        Assert.Equal(1, fixture.Actuations);
    }

    [Fact]
    public async Task InvokeAsync_ConcurrentRetry_RefusesWhileFirstActuationIsRunning()
    {
        using var fixture = new Fixture();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.BeforeActuation = async () => { started.SetResult(); await finish.Task; };
        var first = fixture.Context();
        var running = fixture.InvokeAsync(first);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var retry = fixture.Context();
        try
        {
            await fixture.InvokeAsync(retry);
            Assert.Equal(409, retry.Response.StatusCode);
            Assert.Contains("retry later", ReadBody(retry), StringComparison.Ordinal);
        }
        finally
        {
            finish.TrySetResult();
        }
        await running;
        var completedRetry = fixture.Context();
        await fixture.InvokeAsync(completedRetry);
        Assert.Equal(ReadBody(first), ReadBody(completedRetry));
        Assert.Equal(1, fixture.Actuations);
    }

    private static string ReadBody(HttpContext context) => Encoding.UTF8.GetString(((MemoryStream)context.Response.Body).ToArray());

    private sealed class Fixture : IDisposable
    {
        private readonly ServiceProvider _services;
        private readonly OperationDispatcher _dispatcher;
        public ITenantContext Tenant { get; } = Substitute.For<ITenantContext>();
        public VolatileOperationInstanceStore Store { get; } = new();
        public int Actuations { get; private set; }
        public bool AuditAvailable { get; set; } = true;
        public Func<Task>? BeforeActuation { get; set; }

        public Fixture()
        {
            Tenant.TenantId.Returns("tenant-a");
            var auth = Substitute.For<IAuthorizationService>();
            auth.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object>(), Arg.Any<string>()).Returns(AuthorizationResult.Success());
            var services = new ServiceCollection().AddLogging().AddSingleton(Tenant).AddSingleton(auth);
            var catalog = Substitute.For<IOperationCatalog>();
            catalog.GetDescriptorAsync("admin.import.upload-url", Arg.Any<CancellationToken>())
                .Returns(AdminConnectImportOperationCatalog.Descriptors.Single(item => item.OperationId == "admin.import.upload-url"));
            var executor = Substitute.For<IOperationExecutor>();
            executor.OperationId.Returns("admin.import.upload-url");
            executor.ValidateAsync(Arg.Any<OperationRequest>(), Arg.Any<CancellationToken>())
                .Returns(new OperationValidation { IsValid = true, Status = "valid" });
            executor.SubmitAsync(Arg.Any<OperationRequest>(), Arg.Any<OperationPolicyContext>(), Arg.Any<CancellationToken>())
                .Returns(async call =>
                {
                    if (BeforeActuation is not null) await BeforeActuation();
                    Actuations++;
                    var context = call.ArgAt<OperationPolicyContext>(1);
                    return new OperationHandle
                    {
                        OperationInstanceId = context.OperationInstanceId!, OperationId = "admin.import.upload-url",
                        CorrelationId = context.CorrelationId!, Status = OperationHandleStatus.Queued,
                        CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow, JobId = $"job-{Actuations}",
                        Result = new OperationResultSummary
                        {
                            Summary = "Import queued",
                            Details = new Dictionary<string, string>
                            {
                                ["statusCode"] = "202", ["response"] = JsonSerializer.Serialize(new { jobId = $"job-{Actuations}" }),
                            },
                        },
                    };
                });
            var policy = Substitute.For<IOperationPolicyDecisionPoint>();
            policy.EvaluateAsync(Arg.Any<OperationDescriptor>(), Arg.Any<OperationRequest>(), Arg.Any<OperationPolicyContext>(), Arg.Any<CancellationToken>())
                .Returns(PolicyDecision.Allowed);
            var audit = Substitute.For<IAuditLog>();
            audit.RecordAsync(Arg.Any<AuditEvent>(), Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult<string?>(AuditAvailable ? "audit-identity" : null));
            var verifier = Substitute.For<IOperationApprovalReplayVerifier>();
            verifier.VerifyAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);
            _dispatcher = new OperationDispatcher(catalog, [executor], policy, TimeProvider.System, instanceStore: Store, auditLog: audit, approvalReplayVerifier: verifier);
            _services = services.AddSingleton<IOperationInvoker>(_dispatcher).BuildServiceProvider();
        }

        public DefaultHttpContext Context(string path = "/api/v1/admin/import/upload-url", string body = Body, string actor = "operator-a", string? scope = null, string key = "same-key")
        {
            List<Claim> claims = [new("sub", actor), new(ClaimTypes.Role, "admin")];
            if (scope is not null) claims.Add(new Claim("scp", scope));
            var context = new DefaultHttpContext { RequestServices = _services, User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer")) };
            context.SetEndpoint(new Endpoint(_ => Task.CompletedTask, new EndpointMetadataCollection(new UrlImportOperationMetadata()), "URL import"));
            context.Request.Method = "POST"; context.Request.Path = path; context.Request.Headers["Idempotency-Key"] = key;
            context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body)); context.Response.Body = new MemoryStream();
            return context;
        }

        public Task InvokeAsync(HttpContext context) => new UrlImportOperationMiddleware(_ => throw new InvalidOperationException("The raw actuator must not run on the initial request."))
            .InvokeAsync(context);

        public Task<OperationHandle> ReplayAsync(OperationHandle accepted) => _dispatcher.SubmitAsync(
            new OperationRequest { OperationId = accepted.OperationId },
            new OperationPolicyContext
            {
                OperationInstanceId = accepted.OperationInstanceId, ApprovedProposalId = "approved-proposal", ApprovedPlanHash = "sealed-plan-hash",
                TenantId = accepted.TenantId, PrincipalId = "operator-a",
            });

        public void Dispose() => _services.Dispose();
    }
}

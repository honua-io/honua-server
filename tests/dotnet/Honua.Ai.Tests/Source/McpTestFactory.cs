// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using System.Text.Json;
using Honua.Core.Features.Licensing.Abstractions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Studio.Abstractions;
using Honua.Core.Features.Studio.Domain;
using Honua.Core.Features.Operations.Domain;
using Honua.Ai.Protocols.Mcp.Models;
using Honua.TestKit.Helpers;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Protocols.Mcp;

/// <summary>
/// Shared helpers for MCP unit tests. Creates authenticated HTTP contexts,
/// canonical plan inputs, and JSON-element wrappers for tool arguments.
/// </summary>
internal static class McpTestFactory
{
    public static DefaultHttpContext AuthenticatedHttpContext(
        string user = "test-user",
        HonuaEdition edition = HonuaEdition.Pro) => new()
        {
            RequestServices = CreateRequestServices(edition),
            User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, user)], "Test"))
        };

    public static DefaultHttpContext AnonymousHttpContext(
        HonuaEdition edition = HonuaEdition.Pro) => new()
        {
            RequestServices = CreateRequestServices(edition)
        };

    /// <summary>
    /// Authenticated HTTP context whose <c>RequestServices</c> additionally
    /// registers <paramref name="configureServices"/> — for tools that resolve
    /// collaborators per-request from <c>httpContext.RequestServices</c>
    /// instead of taking them as constructor dependencies (the pattern the
    /// Studio tools use for services registered <c>Scoped</c>, to avoid a
    /// singleton tool capturing a scoped service as a captive dependency;
    /// PR #3016 review). Tools whose collaborators are stateless singletons —
    /// <c>CreateMapPackageTool</c> and <c>CreateAppPackageTool</c> since
    /// ADR-0076 — take them by constructor injection instead, so a missing
    /// registration fails at startup rather than silently.
    /// </summary>
    public static DefaultHttpContext AuthenticatedHttpContextWithServices(
        Action<IServiceCollection> configureServices,
        string user = "test-user",
        HonuaEdition edition = HonuaEdition.Pro) => new()
        {
            RequestServices = CreateRequestServices(edition, configureServices),
            User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, user)], "Test"))
        };

    public static McpPlanInput CreateValidPlanInput() => new()
    {
        PlanId = "plan-1",
        IntentId = "intent-1",
        Steps = new List<McpPlanStepInput>
        {
            new()
            {
                StepId = "step-1",
                Kind = nameof(AnalysisPlanStepKind.Geoprocess),
                ProcessId = "buffer"
            }
        },
        Outputs = new List<string> { nameof(ArtifactKind.FeatureLayer) }
    };

    public static JsonElement ToArguments<T>(T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
    {
        var json = JsonSerializer.Serialize(value, typeInfo);
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    public static JsonElement ParseJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    /// <summary>
    /// Adds a permissive Studio owner-policy test double for legacy tool tests
    /// whose subject is lifecycle delegation rather than authorization. New
    /// authorization tests register an explicit policy decision instead.
    /// </summary>
    public static void AddAllowingStudioAuthorization(IServiceCollection services) =>
        services.AddSingleton<IStudioAuthorizationService, AllowingStudioAuthorizationService>();

    private static ServiceProvider CreateRequestServices(HonuaEdition edition, Action<IServiceCollection>? configureServices = null)
    {
        var license = new TestLicenseEntitlementService(edition);
        var services = new ServiceCollection()
            .AddSingleton<ILicenseEntitlementService>(license)
            .AddSingleton<ILicenseStatusProvider>(license);
        configureServices?.Invoke(services);
        if (services.Any(descriptor => descriptor.ServiceType == typeof(IStudioPackageLifecycleService))
            && !services.Any(descriptor => descriptor.ServiceType == typeof(IStudioDraftMutationRuntime)))
        {
            services.AddSingleton<IStudioDraftMutationRuntime>(provider =>
                new DirectStudioDraftMutationTestRuntime(
                    provider.GetRequiredService<IStudioPackageLifecycleService>()));
        }
        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Explicit test-only adapter for legacy MCP unit fixtures. Production composition registers
    /// the durable operation runtime and can never reach this direct lifecycle adapter.
    /// </summary>
    private sealed class DirectStudioDraftMutationTestRuntime(IStudioPackageLifecycleService lifecycle)
        : IStudioDraftMutationRuntime
    {
        public async Task<StudioDraftMutationReceipt<StudioPackageDraft>> CreateAsync(
            CreateStudioPackageDraftCommand command,
            StudioDraftMutationContext context,
            CancellationToken cancellationToken = default) => Receipt(
                await lifecycle.CreateDraftAsync(command, cancellationToken).ConfigureAwait(false),
                "studio.draft.create");

        public async Task<StudioDraftMutationReceipt<StudioPackageDraft>> UpdateAsync(
            Guid draftId,
            UpdateStudioPackageDraftCommand command,
            StudioDraftMutationContext context,
            CancellationToken cancellationToken = default) => Receipt(
                await lifecycle.UpdateDraftAsync(draftId, command, cancellationToken).ConfigureAwait(false),
                "studio.draft.update");

        public async Task<StudioDraftMutationReceipt<bool>> DeleteAsync(
            Guid draftId,
            StudioDraftMutationContext context,
            CancellationToken cancellationToken = default) => Receipt(
                await lifecycle.DeleteDraftAsync(draftId, cancellationToken).ConfigureAwait(false),
                "studio.draft.delete");

        public async Task<StudioDraftMutationReceipt<StudioValidationSummary>> ValidateAsync(
            Guid draftId,
            string? actorId,
            StudioDraftMutationContext context,
            CancellationToken cancellationToken = default) => Receipt(
                await lifecycle.ValidateDraftAsync(draftId, actorId, cancellationToken).ConfigureAwait(false),
                "studio.draft.validate");

        public async Task<StudioDraftMutationReceipt<StudioPreviewPlan>> PreviewAsync(
            Guid draftId,
            string? actorId,
            StudioDraftMutationContext context,
            CancellationToken cancellationToken = default) => Receipt(
                await lifecycle.PreviewPlanAsync(draftId, actorId, cancellationToken).ConfigureAwait(false),
                "studio.draft.preview-plan");

        public async Task<StudioDraftMutationReceipt<StudioContentVersion>> SaveVersionAsync(
            Guid draftId,
            long expectedGeneration,
            string? changeNote,
            string? actorId,
            StudioDraftMutationContext context,
            CancellationToken cancellationToken = default) => Receipt(
                await lifecycle.SaveDraftAsVersionAsync(
                    draftId, changeNote, actorId, expectedGeneration, cancellationToken).ConfigureAwait(false),
                "studio.draft.save-version");

        public async Task<StudioDraftMutationReceipt<StudioPublicationRequest>> CreatePublicationRequestAsync(
            Guid itemId,
            Guid versionId,
            StudioPublicationIntent? intent,
            string? warningAcknowledgement,
            string? actorId,
            StudioDraftMutationContext context,
            CancellationToken cancellationToken = default) => Receipt(
                await lifecycle.CreatePublicationRequestAsync(
                    itemId, versionId, intent, warningAcknowledgement, actorId, cancellationToken).ConfigureAwait(false),
                "studio.content.create-publication-request");

        public Task<StudioDraftMutationReceipt<StudioPublicationRequest>> CreatePublicationRequestAsync(
            Guid itemId,
            Guid versionId,
            string contentHash,
            StudioPublicationIntent? intent,
            string? warningAcknowledgement,
            string? actorId,
            StudioDraftMutationContext context,
            CancellationToken cancellationToken = default)
        {
            var now = DateTimeOffset.UtcNow;
            return Task.FromResult(new StudioDraftMutationReceipt<StudioPublicationRequest>
            {
                Operation = new OperationHandle
                {
                    OperationInstanceId = "opinst-studio-publication",
                    OperationId = "studio.content.create-publication-request",
                    CorrelationId = context.CorrelationId ?? "corr-studio-publication",
                    AuditId = "audit-studio-publication",
                    ProposalId = "proposal-studio-publication",
                    Status = OperationHandleStatus.RequiresApproval,
                    CreatedAt = now,
                    UpdatedAt = now,
                },
            });
        }

        public async Task<StudioDraftMutationReceipt<StudioPackageDraft>> ReopenVersionAsync(
            Guid itemId,
            Guid versionId,
            string? actorId,
            StudioDraftMutationContext context,
            CancellationToken cancellationToken = default) => Receipt(
                await lifecycle.ReopenVersionAsync(itemId, versionId, actorId, cancellationToken).ConfigureAwait(false),
                "studio.content.reopen-version");

        public async Task<StudioDraftMutationReceipt<StudioRollbackRequest>> RollbackAsync(
            Guid itemId,
            Guid targetVersionId,
            StudioRollbackPointer target,
            string? actorId,
            string? reason,
            StudioDraftMutationContext context,
            CancellationToken cancellationToken = default) => Receipt(
                await lifecycle.RollbackAsync(
                    itemId, targetVersionId, target, actorId, reason, cancellationToken).ConfigureAwait(false),
                "studio.content.rollback");

        private static StudioDraftMutationReceipt<T> Receipt<T>(T? value, string operationId)
        {
            var now = DateTimeOffset.UtcNow;
            return new StudioDraftMutationReceipt<T>
            {
                Operation = new OperationHandle
                {
                    OperationInstanceId = $"opinst-test-{Guid.NewGuid():N}",
                    OperationId = operationId,
                    CorrelationId = $"corr-test-{Guid.NewGuid():N}",
                    AuditId = $"audit-test-{Guid.NewGuid():N}",
                    Status = OperationHandleStatus.Completed,
                    CreatedAt = now,
                    UpdatedAt = now,
                },
                Value = value,
            };
        }
    }

    private sealed class AllowingStudioAuthorizationService : IStudioAuthorizationService
    {
        public bool IsEndUserAuthorizationEnabled => true;

        public bool IsAdmin(ClaimsPrincipal principal) => principal.IsInRole("admin");

        public string? ResolveCallerId(ClaimsPrincipal principal) =>
            principal.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? principal.Identity?.Name;

        public Task<StudioAuthorizationDecision> AuthorizeAsync(
            ClaimsPrincipal principal,
            string? callerId,
            StudioAuthorizationOperation operation,
            string? resourceOwnerId,
            bool isPubliclyReadable = false,
            string? resourceId = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(StudioAuthorizationDecision.Allow());
    }
}

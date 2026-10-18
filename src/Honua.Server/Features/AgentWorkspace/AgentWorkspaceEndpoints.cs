// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Text;
using Honua.Ai.Protocols.Mcp.Tools;
using Honua.Core.Features.AgentWorkspace.Abstractions;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;
using Honua.Core.Features.Operations.Services;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Security;
using Honua.Server.Features.Admin;
using Honua.Server.Features.Operations;
using StackExchange.Redis;

namespace Honua.Server.Features.AgentWorkspace;

internal static class AgentWorkspaceEndpoints
{
    public static void AddAgentWorkspace(this IServiceCollection services)
    {
        services.AddSingleton<IAgentWorkspaceStore>(sp => new AgentWorkspaceStore(sp.GetService<IConnectionMultiplexer>(),
            sp.GetRequiredService<IConfiguration>()["AgentWorkspace:DeploymentId"]));
        services.AddScoped<IAgentWorkspaceTurnAuthorizer, AgentWorkspaceTurnAuthorizer>();
        services.AddHttpClient<ILocalProcessWorkspaceGateway, LocalProcessWorkspaceGateway>(client => client.Timeout = TimeSpan.FromSeconds(30))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, UseProxy = false });
        services.AddSingleton<IMcpToolSource, AgentWorkspaceToolSource>();
        services.AddSingleton<IMcpToolSource, LocalProcessWorkspaceToolSource>();
    }

    public static void MapAgentWorkspace(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v{version:apiVersion}/agent").WithApiVersionSet().HasApiVersion(1, 0)
            .RequireAuthorization().WithTags("Agent workspace");
        group.AddEndpointFilter(async (invocation, next) =>
        {
            var context = invocation.HttpContext;
            context.Response.Headers.CacheControl = "no-store";
            var bodyLimit = context.Request.Path.Value?.Contains("/processes/local/", StringComparison.Ordinal) == true ? 2 * 1024 * 1024 : 65536;
            if (context.Request.ContentLength > bodyLimit)
            {
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            }
            if (!await OperationAdminAuthorization.IsAuthorizedAsync(context, context.User,
                    OperationSideEffectClass.ReadOnly, context.RequestAborted).ConfigureAwait(false))
            {
                return Results.Forbid();
            }
            try
            {
                return await next(invocation).ConfigureAwait(false);
            }
            catch (AgentWorkspaceConflictException exception)
            {
                return Results.Conflict(new AgentWorkspaceError(exception.Message));
            }
            catch (AgentWorkspaceNotFoundException)
            {
                return Results.NotFound();
            }
            catch (LocalProcessWorkspaceException exception)
            {
                return Results.Json(new AgentWorkspaceError(exception.Message), AgentWorkspaceJsonContext.Default.AgentWorkspaceError,
                    statusCode: exception.StatusCode);
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new AgentWorkspaceError(exception.Message));
            }
            catch (Exception exception) when (exception is RedisException or AgentWorkspaceUnavailableException)
            {
                return Results.Json(new AgentWorkspaceError("Durable workspace storage is unavailable."),
                    AgentWorkspaceJsonContext.Default.AgentWorkspaceError, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });
        group.MapGet("/target", async (IAgentWorkspaceStore store) => Results.Ok(new AgentTargetResponse(await store.GetTargetIdAsync().ConfigureAwait(false))));
        group.MapPost("/devops/authorize", AuthorizeDevOps);
        group.MapPost("/workspaces", CreateWorkspace);
        group.MapGet("/workspaces/{workspaceId}", GetWorkspace);
        group.MapPut("/workspaces/{workspaceId}/context", UpdateContext);
        group.MapPost("/workspaces/{workspaceId}/turns", CreateTurn);
        group.MapGet("/workspaces/{workspaceId}/turns/{turnId}", GetTurn);
        group.MapGet("/workspaces/{workspaceId}/conversation", GetConversation);
        group.MapPost("/workspaces/{workspaceId}/conversation", AppendMessage);
        group.MapPost("/workspaces/{workspaceId}/drafts", CreateDraft);
        group.MapGet("/workspaces/{workspaceId}/drafts/{draftId}", GetDraft);
        group.MapPatch("/workspaces/{workspaceId}/drafts/{draftId}", PatchDraft);
        group.MapPost("/workspaces/{workspaceId}/drafts/{draftId}/submit", SubmitDraft);
        group.MapGet("/workspaces/{workspaceId}/commands/{commandId}", GetCommand);
        group.MapLocalProcesses();
    }

    internal static async Task<IResult> AuthorizeDevOps(HttpContext context, IAgentWorkspaceStore store, AgentDevOpsAuthorizationRequest request)
    {
        var readOnly = request.CanonicalTool is null or "describe_environment" or "find_recent_operations" or
            "plan_gitops_engine" or "explain_metadata_release_changeset" or "plan_metadata_release_gitops" or
            "get_gitops_proposal" or "get_devops_operation_status";
        var known = readOnly || request.CanonicalTool is "provision_infrastructure" or "install_handoff" or
            "verify_install_handoff" or "generate_metadata_release_changeset" or "create_gitops_proposal" or
            "deploy_service_gitops" or "create_scoped_gitops_pull_request" or "revert_scoped_gitops_pull_request";
        if (!known || request.TargetId != await store.GetTargetIdAsync().ConfigureAwait(false) ||
            !await OperationAdminAuthorization.IsAuthorizedAsync(context, context.User,
                readOnly ? OperationSideEffectClass.ReadOnly : OperationSideEffectClass.MutatesMetadata,
                context.RequestAborted).ConfigureAwait(false)) return Results.Forbid();
        if (string.IsNullOrWhiteSpace(request.DeployTargetId) || request.DeployTargetId.Length > 128 || request.DeployTargetId.Any(char.IsControl) || string.IsNullOrWhiteSpace(request.Environment) ||
            PlatformDeployAuthority.Deny(context) is not null) return Results.Forbid();
        var registry = context.RequestServices.GetService<IDeployTargetRegistry>();
        if (registry is null) return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        var target = await registry.GetAsync(request.DeployTargetId, context.RequestAborted).ConfigureAwait(false);
        if (target is null || !string.Equals(target.Environment, request.Environment, StringComparison.Ordinal)) return Results.Forbid();
        // A target kind alone is insufficient: Kubernetes GitOps passthrough is
        // not the native Deployment actuator, and runtime identity comes from this registry.
        var runtimeKind = (target.TargetKind, target.Backend) switch
        {
            (DeployTargetKind.AwsEcs, "honua-aws-ecs-alb") => "aws-ecs",
            (DeployTargetKind.AwsLambda, "honua-gitops-aws-lambda") => "aws-lambda",
            (DeployTargetKind.Kubernetes, "honua-kubernetes-deployment") => "aws-eks",
            _ => null
        };
        if (runtimeKind is null) return Results.Forbid();
        var hasRuntimeMarker = target.Parameters.TryGetValue("honua.runtime_kind", out var runtimeMarker);
        if (hasRuntimeMarker && runtimeMarker != runtimeKind || runtimeKind == "aws-eks" && !hasRuntimeMarker)
            return Results.Forbid();
        if (request.CanonicalTool == "provision_infrastructure" &&
            (request.Arguments is null || !request.Arguments.TryGetValue("stack", out var stack) ||
             stack.ValueKind != System.Text.Json.JsonValueKind.String || stack.GetString() != runtimeKind)) return Results.Forbid();
        var gate = context.RequestServices.GetRequiredService<OperatorApprovalGate>();
        var permission = await gate.CheckAuthorizationAsync(context.User, new OperatorAuthorizationRequest
        {
            ResourceType = OperatorResourceType.Deployment,
            Operation = readOnly ? OperatorOperation.Read : OperatorOperation.Publish,
            ResourceId = target.TargetId,
        }, context.RequestAborted).ConfigureAwait(false);
        if (!permission.IsAllowed || !DevOpsArgumentsMatchEnvironment(request)) return Results.Forbid();
        if (request.CanonicalTool is "get_gitops_proposal" or "get_devops_operation_status")
        {
            var operationId = request.Arguments?.FirstOrDefault(pair =>
                string.Equals(pair.Key, "operationId", StringComparison.OrdinalIgnoreCase)).Value;
            if (operationId is not { ValueKind: System.Text.Json.JsonValueKind.String } ||
                operationId.Value.GetString() is not { Length: > 0 and <= 200 } operationKey || operationKey.Any(char.IsControl)) return Results.Forbid();
            var operations = context.RequestServices.GetService<IWorkflowOperationStore>();
            if (operations is null) return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            var operation = await operations.GetAsync(operationKey, context.RequestAborted).ConfigureAwait(false);
            var tenantOptions = context.RequestServices.GetService<Microsoft.Extensions.Options.IOptions<Honua.Infrastructure.MultiTenancy.TenantContextOptions>>()?.Value;
            var tenant = PlatformDeployAuthority.ResolveTenantId(context.User, tenantOptions);
            if (operation?.Deploy is not { } deploy || deploy.TargetId != target.TargetId ||
                deploy.Environment != target.Environment || deploy.TargetKind != target.TargetKind ||
                deploy.Backend != target.Backend || operation.Audit.TenantId != tenant) return Results.Forbid();
        }
        return Results.Ok(new AgentDevOpsAuthorizationResponse(request.TargetId, target.TargetId, target.Environment, runtimeKind, target.Backend));
    }

    private static bool DevOpsArgumentsMatchEnvironment(AgentDevOpsAuthorizationRequest request)
    {
        if (request.Arguments is null) return request.CanonicalTool is null or "describe_environment" or "find_recent_operations";
        if (request.Arguments.Count > 32) return false;
        var arguments = new Dictionary<string, System.Text.Json.JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var argument in request.Arguments)
        {
            if (!arguments.TryAdd(argument.Key, argument.Value)) return false;
        }
        static bool Matches(System.Text.Json.JsonElement value, string environment)
        {
            if (value.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                var environments = value.GetString()!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                return environments.Length > 0 && environments.All(item => string.Equals(item, environment, StringComparison.OrdinalIgnoreCase));
            }
            return value.ValueKind == System.Text.Json.JsonValueKind.Array && value.GetArrayLength() > 0 &&
                value.EnumerateArray().All(item => item.ValueKind == System.Text.Json.JsonValueKind.String &&
                    string.Equals(item.GetString(), environment, StringComparison.OrdinalIgnoreCase));
        }
        if (arguments.TryGetValue("environmentsCsv", out var csv) && !Matches(csv, request.Environment)) return false;
        if (request.CanonicalTool is "plan_gitops_engine" or "create_gitops_proposal" or "deploy_service_gitops" or "revert_scoped_gitops_pull_request")
            return arguments.TryGetValue("environmentsCsv", out csv) && Matches(csv, request.Environment);
        if (request.CanonicalTool == "provision_infrastructure")
        {
            if (!arguments.TryGetValue("variablesJson", out var variables) || variables.ValueKind != System.Text.Json.JsonValueKind.String) return false;
            try
            {
                using var document = System.Text.Json.JsonDocument.Parse(variables.GetString()!);
                return document.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object &&
                    document.RootElement.TryGetProperty("environment", out var environment) && Matches(environment, request.Environment);
            }
            catch (System.Text.Json.JsonException) { return false; }
        }
        if (request.CanonicalTool is "generate_metadata_release_changeset" or "explain_metadata_release_changeset" or "plan_metadata_release_gitops" or "create_scoped_gitops_pull_request")
        {
            if (!arguments.TryGetValue("releasePackageJson", out var package) || package.ValueKind != System.Text.Json.JsonValueKind.String) return false;
            try
            {
                using var document = System.Text.Json.JsonDocument.Parse(package.GetString()!);
                if (document.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object) return false;
                var found = false;
                foreach (var property in document.RootElement.EnumerateObject())
                {
                    if (!string.Equals(property.Name, "targetEnvironments", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(property.Name, "environments", StringComparison.OrdinalIgnoreCase)) continue;
                    found = true;
                    if (!Matches(property.Value, request.Environment)) return false;
                }
                return found;
            }
            catch (System.Text.Json.JsonException) { return false; }
        }
        return true;
    }

    private static string Actor(HttpContext context)
        => CanonicalSecurityActor.Resolve(context.User)?.ActorId ?? throw new AgentWorkspaceNotFoundException();

    private static string? Tenant(HttpContext context)
        => context.RequestServices.GetService<ITenantContext>()?.TenantId;

    private static async Task<AgentWorkspaceDocument> Read(HttpContext context, IAgentWorkspaceStore store)
    {
        var id = context.Request.RouteValues["workspaceId"]?.ToString();
        if (!AgentWorkspaceState.IsIdentifier(id)) throw new AgentWorkspaceNotFoundException();
        var document = await store.GetAsync(id!).ConfigureAwait(false);
        if (document is null || !AgentWorkspaceState.CanRead(document, Actor(context), Tenant(context),
                await store.GetTargetIdAsync().ConfigureAwait(false)))
        {
            throw new AgentWorkspaceNotFoundException();
        }
        return document;
    }

    private static async Task Save(IAgentWorkspaceStore store, AgentWorkspaceDocument document)
    {
        if (!await store.SaveAsync(document).ConfigureAwait(false))
        {
            throw new AgentWorkspaceConflictException("The workspace changed concurrently. Read its current revision before retrying.");
        }
    }

    internal static async Task<IResult> CreateWorkspace(HttpContext context, IAgentWorkspaceStore store, CreateAgentWorkspace request)
    {
        if (!AgentWorkspaceState.IsIdentifier(request.ViewId)) throw new ArgumentException("A view id is required.");
        var targetId = await store.GetTargetIdAsync().ConfigureAwait(false);
        if (request.TargetId is not null && request.TargetId != targetId) throw new ArgumentException("Target does not match this deployment.");
        var owner = Actor(context);
        // Local deployment provisioning may explicitly bind its separate agent principal.
        // No model argument or browser header may choose/replace that identity.
        var linkedAgent = context.User.FindFirst("api_key_id") is null
            ? context.RequestServices.GetRequiredService<IConfiguration>()["AgentWorkspace:LocalAgentPrincipalId"]
            : null;
        var document = new AgentWorkspaceDocument
        {
            WorkspaceId = $"workspace-{Guid.NewGuid():N}",
            TargetId = targetId,
            TenantId = Tenant(context),
            OwnerId = owner,
            Members = string.IsNullOrWhiteSpace(linkedAgent) ? [owner] : [owner, linkedAgent],
        };
        var snapshot = AgentWorkspaceState.Current(document, request.ViewId);
        document.Views[request.ViewId] = [snapshot];
        if (!await store.CreateAsync(document).ConfigureAwait(false)) throw new AgentWorkspaceConflictException("Workspace id collision.");
        return Results.Ok(snapshot);
    }

    internal static async Task<IResult> GetWorkspace(HttpContext context, IAgentWorkspaceStore store, string viewId)
    {
        if (!AgentWorkspaceState.IsIdentifier(viewId)) throw new ArgumentException("An explicit view id is required.");
        var document = await Read(context, store).ConfigureAwait(false);
        var snapshot = AgentWorkspaceState.Current(document, viewId);
        if (!document.Views.ContainsKey(viewId))
        {
            if (document.Views.Count >= 32) throw new ArgumentException("Workspace view limit reached.");
            document.Views.Add(viewId, [snapshot]);
            await Save(store, document).ConfigureAwait(false);
        }
        return Results.Ok(await Project(context, snapshot, document).ConfigureAwait(false));
    }

    private static async Task<AgentWorkspaceContext> Project(HttpContext context, AgentWorkspaceContext snapshot, AgentWorkspaceDocument document)
    {
        var resources = new List<AgentResourceReference>();
        foreach (var resource in snapshot.SelectedResources)
        {
            if (await AgentWorkspaceResourceAuthorization.CanReadAsync(context, resource, context.RequestAborted).ConfigureAwait(false))
            {
                resources.Add(resource);
            }
        }
        var task = await AgentWorkspaceResourceAuthorization.CanReadTaskAsync(context, snapshot.Task, workspace: document).ConfigureAwait(false) ? snapshot.Task : null;
        return snapshot with { SelectedResources = resources.ToArray(), Task = task };
    }

    internal static async Task<IResult> UpdateContext(HttpContext context, IAgentWorkspaceStore store, UpdateAgentContext request)
    {
        var document = await Read(context, store).ConfigureAwait(false);
        if (!await AgentWorkspaceResourceAuthorization.CanReadTaskAsync(context, request.Task, requireGeneration: true, workspace: document).ConfigureAwait(false)) return Results.Forbid();
        var next = AgentWorkspaceState.Update(document, request);
        // Only retain references the writer can actually see; re-evaluate for every reader too.
        var projected = await Project(context, AgentWorkspaceState.Current(next, request.ViewId), next).ConfigureAwait(false);
        if (projected.SelectedResources.Length != request.SelectedResources.Length) return Results.Forbid();
        await Save(store, next).ConfigureAwait(false);
        return Results.Ok(projected);
    }

    internal static async Task<IResult> CreateTurn(HttpContext context, IAgentWorkspaceStore store, CreateAgentTurn request)
    {
        var document = await Read(context, store).ConfigureAwait(false);
        var next = AgentWorkspaceState.AddTurn(document, Actor(context), request);
        if (!ReferenceEquals(document, next))
        {
            if (!await AgentWorkspaceResourceAuthorization.CanReadTaskAsync(context, next.Turns[request.TurnId].Context.Task, requireGeneration: true, workspace: next).ConfigureAwait(false)) return Results.Forbid();
            var turn = next.Turns[request.TurnId];
            if (turn.Context.Task is { Kind: "local-process-workspace" } task)
            {
                if (LocalProcessWorkspaceAuthorization.TrustedAccess(context, next) is not { } access) return Results.Forbid();
                var snapshot = await context.RequestServices.GetRequiredService<ILocalProcessWorkspaceGateway>()
                    .SnapshotAsync(access, new(task.Id, task.Revision, task.ContentHash!), context.RequestAborted).ConfigureAwait(false);
                if (snapshot.ProjectId != task.Id || snapshot.Revision != task.Revision || snapshot.SnapshotDigest != task.ContentHash)
                    throw new LocalProcessWorkspaceException(502, "The captured local process does not match the submitted selection.");
                next.Turns[request.TurnId] = turn with { LocalProcessSnapshot = snapshot };
            }
            await Save(store, next).ConfigureAwait(false);
        }
        return await TurnResponse(context, next, next.Turns[request.TurnId]).ConfigureAwait(false);
    }

    internal static async Task<IResult> GetTurn(HttpContext context, IAgentWorkspaceStore store)
    {
        var document = await Read(context, store).ConfigureAwait(false);
        var turnId = context.Request.RouteValues["turnId"]?.ToString() ?? string.Empty;
        if (!document.Turns.TryGetValue(turnId, out var turn)) return Results.NotFound();
        return await TurnResponse(context, document, turn).ConfigureAwait(false);
    }

    private static async Task<IResult> TurnResponse(HttpContext context, AgentWorkspaceDocument document, AgentWorkspaceTurn turn)
    {
        var projected = await Project(context, turn.Context, document).ConfigureAwait(false);
        if (projected.SelectedResources.Length != turn.Context.SelectedResources.Length || projected.Task != turn.Context.Task) return Results.Forbid();
        if (!await LocalProcessWorkspaceAuthorization.CanReadTurnAsync(context, document, turn).ConfigureAwait(false)) return Results.Forbid();
        return Results.Ok(new AgentTurnResponse(turn.TurnId, "accepted", projected,
            await VisibleMessages(context, document).ConfigureAwait(false), turn.Draft, turn.LocalProcessSnapshot));
    }

    private static async Task<AgentWorkspaceMessage[]> VisibleMessages(HttpContext context, AgentWorkspaceDocument document)
    {
        var messages = new List<AgentWorkspaceMessage>();
        var visibleTurns = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var message in document.Messages)
        {
            if (!visibleTurns.TryGetValue(message.TurnId, out var visible))
            {
                if (!document.Turns.TryGetValue(message.TurnId, out var turn)) continue;
                var permitted = await Project(context, turn.Context, document).ConfigureAwait(false);
                // Conversation can name selected resources. Verify each immutable turn once
                // per response; repeated messages must not rehash the same process snapshot.
                visible = permitted.SelectedResources.Length == turn.Context.SelectedResources.Length && permitted.Task == turn.Context.Task &&
                    await LocalProcessWorkspaceAuthorization.CanReadTurnAsync(context, document, turn).ConfigureAwait(false);
                visibleTurns[message.TurnId] = visible;
            }
            if (visible) messages.Add(message);
        }
        return messages.ToArray();
    }

    internal static async Task<IResult> GetConversation(HttpContext context, IAgentWorkspaceStore store)
    {
        var document = await Read(context, store).ConfigureAwait(false);
        return Results.Ok(new AgentConversationResponse(await VisibleMessages(context, document).ConfigureAwait(false)));
    }

    internal static async Task<IResult> AppendMessage(HttpContext context, IAgentWorkspaceStore store, AppendAgentMessage request)
    {
        var document = await Read(context, store).ConfigureAwait(false);
        if (!context.User.HasClaim("permission", "agent:workspace")) return Results.Forbid();
        if (!AgentWorkspaceState.IsIdentifier(request.MessageId) || !AgentWorkspaceState.IsIdentifier(request.TurnId) ||
            string.IsNullOrWhiteSpace(request.Content) || request.Content.Length > 16000 || !document.Turns.TryGetValue(request.TurnId, out var turn))
            throw new ArgumentException("A known turn, message id and bounded content are required.");
        var projected = await Project(context, turn.Context, document).ConfigureAwait(false);
        if (projected.SelectedResources.Length != turn.Context.SelectedResources.Length || projected.Task != turn.Context.Task) return Results.Forbid();
        var existing = document.Messages.SingleOrDefault(message => message.Id == request.MessageId);
        if (existing is not null)
        {
            if (existing.Role != "assistant" || existing.TurnId != request.TurnId || existing.Content != request.Content)
                throw new AgentWorkspaceConflictException("Message id is bound to different content.");
            return Results.Ok(existing);
        }
        if (document.Messages.Length >= 1024) throw new ArgumentException("Workspace conversation limit reached.");
        // Model output is explicitly conversation, never an operation status or verified receipt.
        var message = new AgentWorkspaceMessage(request.MessageId, request.TurnId, "assistant", request.Content);
        await Save(store, document with { Messages = [.. document.Messages, message] }).ConfigureAwait(false);
        return Results.Ok(message);
    }

    internal static async Task<IResult> CreateDraft(HttpContext context, IAgentWorkspaceStore store, CreateAgentDraft request)
    {
        var document = await Read(context, store).ConfigureAwait(false);
        if (request.Kind != "publish-layer" || !AgentWorkspaceState.IsIdentifier(request.ViewId)) throw new ArgumentException("Unknown draft kind or view.");
        if (AgentWorkspaceState.Current(document, request.ViewId).Revision != request.ContextRevision) throw new AgentWorkspaceConflictException("The view changed.");
        if (document.Drafts.Count >= 128) throw new ArgumentException("Workspace draft limit reached.");
        var draft = new AgentWorkspaceDraft($"draft-{Guid.NewGuid():N}", 0, request.Kind,
            AgentWorkspaceState.ValidateFields(request.Fields), []);
        document.Drafts.Add(draft.Id, draft);
        var next = SelectDraft(document, request.ViewId, draft);
        await Save(store, next).ConfigureAwait(false);
        return Results.Ok(draft);
    }

    private static AgentWorkspaceDocument SelectDraft(AgentWorkspaceDocument document, string viewId, AgentWorkspaceDraft draft)
    {
        var current = AgentWorkspaceState.Current(document, viewId);
        return AgentWorkspaceState.Update(document, new(viewId, current.Revision, document.TargetId,
            current.SelectedResources, new(draft.Id, draft.Revision), "workflow"));
    }

    private static AgentWorkspaceDraft ReadDraft(HttpContext context, AgentWorkspaceDocument document)
    {
        var id = context.Request.RouteValues["draftId"]?.ToString() ?? string.Empty;
        return document.Drafts.TryGetValue(id, out var draft) ? draft : throw new AgentWorkspaceNotFoundException();
    }

    internal static async Task<IResult> GetDraft(HttpContext context, IAgentWorkspaceStore store)
        => Results.Ok(ReadDraft(context, await Read(context, store).ConfigureAwait(false)));

    internal static async Task<IResult> PatchDraft(HttpContext context, IAgentWorkspaceStore store, PatchAgentDraft request)
    {
        var document = await Read(context, store).ConfigureAwait(false);
        var draft = ReadDraft(context, document);
        var next = AgentWorkspaceState.PatchDraft(document, draft.Id, request);
        await Save(store, next).ConfigureAwait(false);
        return Results.Ok(next.Drafts[draft.Id]);
    }

    internal static async Task<IResult> SubmitDraft(HttpContext context, IAgentWorkspaceStore store, SubmitAgentDraft request)
    {
        var document = await Read(context, store).ConfigureAwait(false);
        if (!AgentWorkspaceState.IsIdentifier(request.CommandId) || !AgentWorkspaceState.IsIdentifier(request.ViewId)) throw new ArgumentException("Command and view ids are required.");
        var draft = ReadDraft(context, document);
        var actor = Actor(context);
        if (document.Commands.TryGetValue(request.CommandId, out var command))
        {
            if (command.ActorId != actor || command.DraftId != draft.Id || command.DraftRevision != request.ExpectedRevision)
                throw new AgentWorkspaceConflictException("Command id is bound to different input or executor.");
        }
        else
        {
            if (draft.Revision != request.ExpectedRevision) throw new AgentWorkspaceConflictException("The shared draft changed.");
            if (document.Commands.Count >= 256) throw new ArgumentException("Workspace command limit reached.");
            var key = "workspace:" + Convert.ToHexStringLower(SHA256.HashData(
                Encoding.UTF8.GetBytes($"{document.WorkspaceId}:{request.CommandId}")));
            command = new(request.CommandId, actor, draft.Id, draft.Revision, key,
                OperationEnvelopeFactory.DeriveIdempotentInstanceId(ServicePublishOperation.OperationId,
                    OperationsEndpoints.ScopeIdempotencyKey(key, document.TenantId, actor), document.TenantId),
                new(draft.Fields, StringComparer.Ordinal), DateTimeOffset.UtcNow.AddDays(1));
            document.Commands.Add(command.CommandId, command);
            // Persist the input and execution identity BEFORE dispatch. A lost response
            // is reconciled against the same canonical operation, never a fresh mutation.
            await Save(store, document).ConfigureAwait(false);
        }
        if (DateTimeOffset.UtcNow >= command.RetryBefore)
            throw new AgentWorkspaceConflictException("The retry window expired. Inspect the recorded command receipt; do not resubmit the mutation.");
        var previousKey = context.Request.Headers["Idempotency-Key"];
        context.Request.Headers["Idempotency-Key"] = command.IdempotencyKey;
        try
        {
            var fields = command.Fields;
            return await OperationsEndpoints.HandleSubmitOperationWithApproval(context, ServicePublishOperation.OperationId,
                new OperationInvokeRequest
                {
                    ConnectionId = fields.GetValueOrDefault("connectionId"),
                    ServiceName = fields.GetValueOrDefault("serviceName"),
                    Parameters = fields.Where(pair => pair.Key is not ("connectionId" or "serviceName"))
                        .ToDictionary(pair => pair.Key, pair => (string?)pair.Value, StringComparer.Ordinal),
                }, context.RequestServices.GetRequiredService<IOperationCatalog>(),
                context.RequestServices.GetRequiredService<IOperationInvoker>(), durableOperatorApproval: true, context.RequestAborted).ConfigureAwait(false);
        }
        finally
        {
            context.Request.Headers["Idempotency-Key"] = previousKey;
        }
    }

    internal static async Task<IResult> GetCommand(HttpContext context, IAgentWorkspaceStore store)
    {
        var document = await Read(context, store).ConfigureAwait(false);
        var commandId = context.Request.RouteValues["commandId"]?.ToString() ?? string.Empty;
        if (!document.Commands.TryGetValue(commandId, out var command)) return Results.NotFound();
        return await OperationsEndpoints.HandleGetHandleStatus(context, command.OperationInstanceId,
            context.RequestServices.GetRequiredService<IOperationInstanceStore>(), context.RequestAborted).ConfigureAwait(false);
    }
}
internal sealed class AgentWorkspaceNotFoundException : Exception;

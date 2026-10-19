// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Honua.Ai.Protocols.Mcp.Models;
using Honua.Ai.Protocols.Mcp.Tools;
using Honua.Core.Features.Operations.Domain;
using Honua.Infrastructure.Authentication;
using Honua.Server.Features.Operations;

namespace Honua.Server.Features.AgentWorkspace;

/// <summary>Projects the installed durable workspace capability into the existing MCP surface.</summary>
internal sealed class AgentWorkspaceToolSource(IAgentWorkspaceStore store, IServiceProviderIsService? capabilities = null) : IMcpToolSource
{
    public ValueTask<IReadOnlyList<IMcpTool>> GetToolsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!store.IsAvailable) return ValueTask.FromResult<IReadOnlyList<IMcpTool>>([]);
        var captureReady = capabilities is not null && capabilities.IsService(typeof(Honua.Core.Features.Authorization.Abstractions.IAgentJobSecurityContextCapture));
        var importReady = capabilities is not null && captureReady && capabilities.IsService(typeof(Honua.Core.Features.Import.Abstractions.IDatasetImportContributorAuthorizer));
        var authorityReady = capabilities?.IsService(typeof(Honua.Core.Features.Import.Abstractions.IDatasetImportAuthorityDescriptorReader)) == true;
        var tools = new List<IMcpTool>(AgentWorkspaceTool.All);
        if (importReady) tools.AddRange(AgentWorkspaceTool.DatasetImportTools);
        if (authorityReady) tools.AddRange(AgentWorkspaceTool.DatasetImportAuthorityReadTools);
        if (authorityReady && captureReady) tools.AddRange(AgentWorkspaceTool.DatasetImportAuthorityRenewTools);
        if (capabilities?.IsService(typeof(DatasetImportSubmissionService)) == true) tools.AddRange(AgentWorkspaceTool.DatasetImportSubmissionTools);
        return ValueTask.FromResult<IReadOnlyList<IMcpTool>>(tools);
    }
}

internal sealed class AgentWorkspaceTool(string action, string description, string properties, string required, bool readOnly) : IMcpTool
{
    private const string Identifier = """{"type":"string","pattern":"^[A-Za-z0-9_-]{1,128}$"}""";
    private const string Fields = """{"type":"object","properties":{"connectionId":{"type":"string","maxLength":256},"schema":{"type":"string","maxLength":256},"table":{"type":"string","maxLength":256},"serviceName":{"type":"string","maxLength":256},"layerName":{"type":"string","maxLength":256}},"additionalProperties":false}""";
    private const string TaskSchema = """{"type":["object","null"],"properties":{"id":{"type":"string"},"revision":{"type":"integer","minimum":0},"kind":{"type":"string","enum":["publish-layer","studio-composition","workflow-package","local-process-workspace","shared-style","dataset-import"]},"packageKey":{"type":"string","minLength":1,"maxLength":256},"contentHash":{"type":"string","pattern":"^[0-9a-f]{64}$"},"impactHash":{"type":"string","pattern":"^[0-9a-f]{64}$"}},"required":["id","revision"],"additionalProperties":false}""";
    private static string Property(string name, string schema) => $"\"{name}\":{schema}";
    private static readonly string Workspace = Property("workspaceId", Identifier);
    private static readonly string View = Property("viewId", Identifier);
    private static readonly string Draft = Property("draftId", Identifier);
    private static readonly string Revision = "\"expectedRevision\":{\"type\":\"integer\",\"minimum\":0}";
    private static readonly string ContextRevision = "\"contextRevision\":{\"type\":\"integer\",\"minimum\":0}";
    internal static readonly IMcpTool[] All =
    [
        new AgentWorkspaceTool("create", "Create a durable workspace under the caller identity. Human pairing is separate.", View, "\"viewId\"", false),
        new AgentWorkspaceTool("get", "Read authorized context for an explicit tab/view. For an active turn use get_turn, which retains the submitted selection.", $"{Workspace},{View}", "\"workspaceId\",\"viewId\"", true),
        new AgentWorkspaceTool("set_context", "Update the selected resource and shared task for future prompts, guarded by view revision.",
            $"{Workspace},{View},{Revision},{Property("targetId", Identifier)},{Property("task", TaskSchema)},\"selectedResources\":{{\"type\":\"array\",\"maxItems\":32,\"items\":{{\"type\":\"object\",\"properties\":{{\"kind\":{{\"const\":\"layer\"}},{Property("id", Identifier)},{Property("serviceId", "{\"type\":\"string\",\"minLength\":1,\"maxLength\":256}")}}},\"required\":[\"kind\",\"id\"],\"additionalProperties\":false}}}}",
            "\"workspaceId\",\"viewId\",\"expectedRevision\",\"targetId\",\"selectedResources\",\"task\"", false),
        new AgentWorkspaceTool("create_turn", "Capture a prompt against its submitted context revision before execution. Reuse the turnId to reconcile a lost response.",
            $"{Workspace},{View},{ContextRevision},{Property("turnId", Identifier)},\"prompt\":{{\"type\":\"string\",\"minLength\":1,\"maxLength\":16000}}",
            "\"workspaceId\",\"viewId\",\"contextRevision\",\"turnId\",\"prompt\"", false),
        new AgentWorkspaceTool("get_turn", "Read the immutable prompt binding and captured draft, filtered under the current agent's permissions.",
            $"{Workspace},{Property("turnId", Identifier)}", "\"workspaceId\",\"turnId\"", true),
        new AgentWorkspaceTool("conversation", "Read the authorized durable workspace conversation.", Workspace, "\"workspaceId\"", true),
        new AgentWorkspaceTool("append_message", "Persist assistant conversation text. This never creates a verified operation receipt.",
            $"{Workspace},{Property("turnId", Identifier)},{Property("messageId", Identifier)},\"content\":{{\"type\":\"string\",\"minLength\":1,\"maxLength\":16000}}",
            "\"workspaceId\",\"turnId\",\"messageId\",\"content\"", false),
        new AgentWorkspaceTool("create_draft", "Start a shared publish-layer form. Credential values are not accepted in this draft.",
            $"{Workspace},{View},{ContextRevision},\"kind\":{{\"const\":\"publish-layer\"}},{Property("fields", Fields)}",
            "\"workspaceId\",\"viewId\",\"contextRevision\",\"kind\",\"fields\"", false),
        new AgentWorkspaceTool("get_draft", "Read the current shared form and revision.", $"{Workspace},{Draft}", "\"workspaceId\",\"draftId\"", true),
        new AgentWorkspaceTool("patch_draft", "Edit permitted shared form fields with optimistic concurrency. Conflicts require reviewing the latest draft.",
            $"{Workspace},{Draft},{View},{Revision},{Property("changes", Fields)}", "\"workspaceId\",\"draftId\",\"viewId\",\"expectedRevision\",\"changes\"", false),
        new AgentWorkspaceTool("submit_draft", "Submit the frozen form through canonical service.publish with current caller RBAC and policy. Persist and reuse commandId; approval-required is not published.",
            $"{Workspace},{Draft},{View},{Revision},{Property("commandId", Identifier)}", "\"workspaceId\",\"draftId\",\"viewId\",\"expectedRevision\",\"commandId\"", false),
        new AgentWorkspaceTool("get_command", "Read or reconcile an existing command in this authorized workspace. Supply exactly one selector: commandId from a workspace draft submission, or operationInstanceId returned by honua_shared_style_propose or honua_workflow_package_propose. Status works from a later chat under the original frozen turn and current authority, and never creates a new mutation or approval. Shared-style reads reconcile committed receipts; workflow reads report persisted status without retrying interrupted publication.",
            $"{Workspace},{Property("commandId", Identifier)},{Property("operationInstanceId", Identifier)}", "\"workspaceId\"", true),
    ];

    private const string DatasetImportRequestSchema = """
        {"type":"object","properties":{
          "intentId":{"type":"string","pattern":"^[A-Za-z0-9_-]{1,128}$"},
          "viewId":{"type":"string","pattern":"^[A-Za-z0-9_-]{1,128}$"},
          "contextRevision":{"type":"integer","minimum":0},
          "destination":{"type":"object","properties":{"schema":{"type":"string","minLength":1,"maxLength":63},"table":{"type":"string","minLength":1,"maxLength":63}},"required":["schema","table"],"additionalProperties":false},
          "source":{"type":"object","properties":{
            "kind":{"type":"string","enum":["browser-file","local-process-artifact"]},
            "fileName":{"type":"string","minLength":1,"maxLength":255},"totalBytes":{"type":"integer","minimum":1},
            "expectedSha256":{"type":["string","null"],"pattern":"^[0-9a-f]{64}$"},
            "browserSelectionId":{"type":["string","null"],"pattern":"^[A-Za-z0-9_-]{1,128}$"},
            "artifact":{"type":["object","null"],"properties":{
              "projectId":{"type":"string","pattern":"^[A-Za-z0-9_-]{1,128}$"},"snapshotId":{"type":"string","pattern":"^[A-Za-z0-9_-]{1,128}$"},
              "snapshotDigest":{"type":"string","pattern":"^[0-9a-f]{64}$"},"runId":{"type":"string","pattern":"^[A-Za-z0-9_-]{1,128}$"},
              "path":{"type":"string","minLength":1,"maxLength":1024}},"required":["projectId","snapshotId","snapshotDigest","runId","path"],"additionalProperties":false}
          },"required":["kind","fileName","totalBytes"],"additionalProperties":false},
          "options":{"type":"object","properties":{"loadMode":{"type":"integer","enum":[0,1]},"targetSrid":{"type":"integer","minimum":1},"sourceSrid":{"type":["integer","null"],"minimum":1}},"additionalProperties":false}
        },"required":["intentId","viewId","contextRevision","destination","source","options"],"additionalProperties":false}
        """;
    internal static readonly IMcpTool[] DatasetImportTools =
    [
        new AgentWorkspaceTool("create_dataset_import_task", "Create a canonical immutable dataset-import task from the original bound turn and authorized source/destination. Persist intentId before calling; recover a lost reply with get_dataset_import_intent. This creates no upload session, job or approval and never grants unsupported existing-table mutation.",
            $"{Workspace},{Property("request", DatasetImportRequestSchema)}", "\"workspaceId\",\"request\"", false),
        new AgentWorkspaceTool("get_dataset_import_intent", "Read the original same-owner import task by its persisted client intentId. This never creates a task or dispatches an upload/job, and never changes focus or the original admission deadline.",
            $"{Workspace},{Property("intentId", Identifier)}", "\"workspaceId\",\"intentId\"", true),
    ];

    private static readonly string DatasetRevision = Property("revision", "{\"type\":\"integer\",\"minimum\":1}");
    internal static readonly IMcpTool[] DatasetImportAuthorityReadTools =
    [
        new AgentWorkspaceTool("get_dataset_import_authority", "Read current scoped authority revision/fingerprints, original deadline and prepared operation metadata for an exact immutable import task. No credentials, claims, authority renewal, upload acceptance or dispatch is produced.",
            $"{Workspace},{Property("taskId", Identifier)},{DatasetRevision},{Property("contentHash", "{\"type\":\"string\",\"pattern\":\"^[0-9a-f]{64}$\"}")}",
            "\"workspaceId\",\"taskId\",\"revision\",\"contentHash\"", true),
    ];
    private const string DatasetAuthorityRenewalSchema = """
        {"type":"object","properties":{"renewalId":{"type":"string","pattern":"^[A-Za-z0-9_-]{1,128}$"},
        "expectedAuthorityRevision":{"type":"integer","minimum":1},"taskContentHash":{"type":"string","pattern":"^[0-9a-f]{64}$"}},
        "required":["renewalId","expectedAuthorityRevision","taskContentHash"],"additionalProperties":false}
        """;
    internal static readonly IMcpTool[] DatasetImportAuthorityRenewTools =
    [
        new AgentWorkspaceTool("renew_dataset_import_authority", "Explicitly renew the same executor's expired or expiring task authority through canonical CAS. Persist renewalId before calling; reuse it to recover a lost response. This never changes task intent, ownership, policy values or original deadline, and cannot renew prepared/accepted job authority.",
            $"{Workspace},{Property("taskId", Identifier)},{DatasetRevision},{Property("request", DatasetAuthorityRenewalSchema)}",
            "\"workspaceId\",\"taskId\",\"revision\",\"request\"", false),
    ];

    private const string DatasetSubmitSchema = """
        {"type":"object","properties":{"taskContentHash":{"type":"string","pattern":"^[0-9a-f]{64}$"},
        "authorityRevision":{"type":"integer","minimum":1},"authorityFingerprint":{"type":"string","pattern":"^[0-9a-f]{64}$"},
        "policyContextFingerprint":{"type":"string","pattern":"^[0-9a-f]{64}$"}},
        "required":["taskContentHash","authorityRevision","authorityFingerprint","policyContextFingerprint"],"additionalProperties":false}
        """;
    internal static readonly IMcpTool[] DatasetImportSubmissionTools =
    [
        new AgentWorkspaceTool("get_dataset_import_submission", "Read the original task preparation or actual accepted durable import job under current observer authority. Preparation alone is not acceptance. This never dispatches or retries a job; successful output names only the actual persisted physical dataset receipt.",
            $"{Workspace},{Property("taskId", Identifier)},{DatasetRevision},{Property("contentHash", "{\"type\":\"string\",\"pattern\":\"^[0-9a-f]{64}$\"}")}",
            "\"workspaceId\",\"taskId\",\"revision\",\"contentHash\"", true),
        new AgentWorkspaceTool("submit_dataset_import", "Explicitly submit the exact retained-source task using its reviewed authority descriptor. Actual agent submission uses canonical durable queue, lease and side-effect/approval policy. Human contribution prepares and awaits the actual agent. Recover unknown outcomes with get_dataset_import_submission before any explicit original replay; never change the task or infer acceptance from preparation.",
            $"{Workspace},{Property("taskId", Identifier)},{DatasetRevision},{Property("request", DatasetSubmitSchema)}",
            "\"workspaceId\",\"taskId\",\"revision\",\"request\"", false),
    ];

    public string Name => $"honua_workspace_{action}";
    public string WorkflowFamily => "lifecycle";
    public McpToolDescriptor Describe() => new()
    {
        Name = Name,
        Description = description,
        InputSchema = ParseJson($"{{\"type\":\"object\",\"properties\":{{{properties}}},\"required\":[{required}],\"additionalProperties\":false" +
            (action == "get_command" ? ",\"oneOf\":[{\"required\":[\"commandId\"]},{\"required\":[\"operationInstanceId\"]}]" : "") + "}"),
        Annotations = new() { ReadOnlyHint = readOnly, DestructiveHint = action == "submit_draft", IdempotentHint = action != "create" && action != "create_draft" },
        Meta = ParseJson("""{"honua.studio":{"family":"honua.workspace.canonical","view":"workspace","revision":"1"}}"""),
    };

    public async Task<McpToolsCallResult> InvokeAsync(HttpContext httpContext, JsonElement? arguments, CancellationToken cancellationToken)
    {
        if (arguments is not { ValueKind: JsonValueKind.Object } args || args.GetRawText().Length > 65536)
            return Error("A bounded argument object is required.");
        if (!await OperationAdminAuthorization.IsAuthorizedAsync(httpContext, httpContext.User,
                OperationSideEffectClass.ReadOnly, cancellationToken).ConfigureAwait(false)) return Error("Permission denied.");
        // Never mutate the shared HTTP request: MCP batches may invoke tools concurrently.
        var context = new DefaultHttpContext { User = httpContext.User, RequestServices = httpContext.RequestServices, RequestAborted = cancellationToken };
        context.Request.Method = readOnly ? "GET" : "POST";
        context.Request.Path = "/api/v1/agent/workspaces";
        foreach (var header in httpContext.Request.Headers) context.Request.Headers[header.Key] = header.Value;
        foreach (var name in new[] { "workspaceId", "draftId", "turnId", "commandId", "operationInstanceId", "intentId", "taskId" })
        {
            if (!args.TryGetProperty(name, out var value)) continue;
            if (value.ValueKind != JsonValueKind.String || !AgentWorkspaceState.IsIdentifier(value.GetString())) return Error("Invalid resource identifier.");
            context.Request.RouteValues[name] = value.GetString();
        }
        if (action == "get_command" && (args.TryGetProperty("commandId", out _) == args.TryGetProperty("operationInstanceId", out _)))
            return Error("Supply exactly one of commandId or operationInstanceId.");
        var boundWorkspace = httpContext.Request.Headers["X-Honua-Workspace"].ToString();
        if (boundWorkspace.Length > 0 && context.Request.RouteValues["workspaceId"]?.ToString() != boundWorkspace)
            return Error("The tool call does not match the active workspace.");
        var boundTurn = httpContext.Request.Headers["X-Honua-Turn"].ToString();
        if (boundTurn.Length > 0 && (action is "get_turn" or "create_turn" or "append_message") &&
            context.Request.RouteValues["turnId"]?.ToString() != boundTurn)
            return Error("The tool call does not match the submitted turn.");
        try
        {
            var store = context.RequestServices.GetRequiredService<IAgentWorkspaceStore>();
            var result = action switch
            {
                "create" => await AgentWorkspaceEndpoints.CreateWorkspace(context, store, Parse(args, AgentWorkspaceJsonContext.Default.CreateAgentWorkspace)).ConfigureAwait(false),
                "get" => await AgentWorkspaceEndpoints.GetWorkspace(context, store, args.GetProperty("viewId").GetString()!).ConfigureAwait(false),
                "set_context" => await AgentWorkspaceEndpoints.UpdateContext(context, store, Parse(args, AgentWorkspaceJsonContext.Default.UpdateAgentContext)).ConfigureAwait(false),
                "create_turn" => await AgentWorkspaceEndpoints.CreateTurn(context, store, Parse(args, AgentWorkspaceJsonContext.Default.CreateAgentTurn)).ConfigureAwait(false),
                "get_turn" => await AgentWorkspaceEndpoints.GetTurn(context, store).ConfigureAwait(false),
                "conversation" => await AgentWorkspaceEndpoints.GetConversation(context, store).ConfigureAwait(false),
                "append_message" => await AgentWorkspaceEndpoints.AppendMessage(context, store, Parse(args, AgentWorkspaceJsonContext.Default.AppendAgentMessage)).ConfigureAwait(false),
                "get_dataset_import_submission" => await DatasetImportSubmissionEndpoints.ReadAsync(context,
                    args.Deserialize(AgentWorkspaceJsonContext.Default.ReadDatasetImportAuthority)).ConfigureAwait(false),
                "submit_dataset_import" => await DatasetImportSubmissionEndpoints.SubmitAsync(context,
                    args.Deserialize(AgentWorkspaceJsonContext.Default.SubmitDatasetImportCall)).ConfigureAwait(false),
                "get_dataset_import_authority" => await DatasetImportTaskEndpoints.ReadAuthorityAsync(context,
                    Parse(args, AgentWorkspaceJsonContext.Default.ReadDatasetImportAuthority)).ConfigureAwait(false),
                "renew_dataset_import_authority" => await DatasetImportTaskEndpoints.RenewAuthorityAsync(context, store,
                    Parse(args, AgentWorkspaceJsonContext.Default.RenewDatasetImportAuthorityCall)).ConfigureAwait(false),
                "create_dataset_import_task" => await DatasetImportTaskService.CreateAsync(args.GetProperty("workspaceId").GetString()!, context, store,
                    Parse(args.GetProperty("request"), AgentWorkspaceJsonContext.Default.CreateDatasetImportTask)).ConfigureAwait(false),
                "get_dataset_import_intent" => await DatasetImportTaskService.ReadIntent(args.GetProperty("workspaceId").GetString()!,
                    args.GetProperty("intentId").GetString()!, context, store).ConfigureAwait(false),
                "create_draft" => await AgentWorkspaceEndpoints.CreateDraft(context, store, Parse(args, AgentWorkspaceJsonContext.Default.CreateAgentDraft)).ConfigureAwait(false),
                "get_draft" => await AgentWorkspaceEndpoints.GetDraft(context, store).ConfigureAwait(false),
                "patch_draft" => await AgentWorkspaceEndpoints.PatchDraft(context, store, Parse(args, AgentWorkspaceJsonContext.Default.PatchAgentDraft)).ConfigureAwait(false),
                "submit_draft" => await AgentWorkspaceEndpoints.SubmitDraft(context, store, Parse(args, AgentWorkspaceJsonContext.Default.SubmitAgentDraft)).ConfigureAwait(false),
                "get_command" => args.TryGetProperty("operationInstanceId", out _)
                    ? await AgentWorkspaceEndpoints.GetOperation(context, store).ConfigureAwait(false)
                    : await AgentWorkspaceEndpoints.GetCommand(context, store).ConfigureAwait(false),
                _ => throw new ArgumentException("Unknown workspace action."),
            };
            if (result is not IValueHttpResult { Value: { } value } || result is IStatusCodeHttpResult { StatusCode: >= 400 }) return Error("Workspace operation refused; inspect the current authorized state.");
            var type = AgentWorkspaceJsonContext.Default.GetTypeInfo(value.GetType()) ?? OperationsJsonContext.Default.GetTypeInfo(value.GetType());
            if (type is null) return Error("The canonical receipt could not be projected. Reconcile the existing command before retrying.");
            var content = JsonSerializer.SerializeToElement(value, type);
            return new() { StructuredContent = content, Content = [new() { Text = content.GetRawText() }] };
        }
        catch (AgentWorkspaceConflictException)
        {
            return Error("Version conflict. Read the latest workspace or reconcile the existing command; do not overwrite newer edits.");
        }
        catch (Exception exception) when (exception is ArgumentException or JsonException or KeyNotFoundException or
            AgentWorkspaceNotFoundException or AgentWorkspaceUnavailableException or LocalProcessWorkspaceException or UnauthorizedAccessException or StackExchange.Redis.RedisException)
        {
            return Error("Workspace request unavailable or unauthorized. Reconcile existing commands before retrying mutations.");
        }
    }

    private static JsonElement ParseJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static T Parse<T>(JsonElement args, JsonTypeInfo<T> type) where T : class
        => JsonSerializer.Deserialize(args, type) ?? throw new ArgumentException("Invalid workspace request.");
    private static McpToolsCallResult Error(string message) => new() { IsError = true, Content = [new() { Text = message }] };
}

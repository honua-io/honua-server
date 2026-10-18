// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using System.Text.Json;
using Honua.Ai.Protocols.Mcp.Models;
using Honua.Ai.Protocols.Mcp.Tools;
using Honua.Core.Features.AgentWorkspace.Abstractions;
using Honua.Core.Features.AgentWorkspace.Domain;

namespace Honua.Server.Features.AgentWorkspace;

internal sealed class LocalProcessWorkspaceToolSource(IAgentWorkspaceStore store, IConfiguration configuration) : IMcpToolSource
{
    public ValueTask<IReadOnlyList<IMcpTool>> GetToolsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<IReadOnlyList<IMcpTool>>(store.IsAvailable &&
            !string.IsNullOrWhiteSpace(configuration["LocalProcesses:WorkspaceUrl"]) &&
            !string.IsNullOrWhiteSpace(configuration["HONUA_GP_WORKSPACE_KEY"]) ? LocalProcessWorkspaceTool.All : []);
    }
}

internal sealed class LocalProcessWorkspaceTool(string action, string description, string fields, string required, bool readOnly) : IMcpTool
{
    private const string Id = """{"type":"string","pattern":"^[A-Za-z0-9_-]{1,128}$"}""";
    private const string ProcessId = """{"type":"string","pattern":"^[a-f0-9]{32}$"}""";
    private const string Digest = """{"type":"string","pattern":"^[a-f0-9]{64}$"}""";
    private const string Path = """{"type":"string","minLength":1,"maxLength":1024}""";
    private static string Field(string name, string schema) => $"\"{name}\":{schema}";
    private static readonly string Project = Field("projectId", ProcessId);
    private static readonly string Revision = "\"expectedRevision\":{\"type\":\"integer\",\"minimum\":1}";
    internal static readonly IMcpTool[] All =
    [
        new LocalProcessWorkspaceTool("get_project", "Read current local project identity and file names/digests. Source reads default to the submitted prompt snapshot. Human binding is required first.", "", "", true),
        new LocalProcessWorkspaceTool("read_source", "Read a source file from the immutable prompt snapshot. Set current=true only to explicitly inspect live edits, such as after a concurrency conflict; this never retargets the prompt.",
            $"{Field("path", Path)},\"current\":{{\"type\":\"boolean\"}}", "\"path\"", true),
        new LocalProcessWorkspaceTool("write_source", "Replace one existing source file with both its observed SHA256 and project revision. A conflict requires reviewing current source. This changes neither the submitted prompt snapshot nor a running job.",
            $"{Project},{Revision},{Field("path", Path)},{Field("expectedSha256", Digest)},\"content\":{{\"type\":\"string\",\"maxLength\":262144}}",
            "\"projectId\",\"expectedRevision\",\"path\",\"expectedSha256\",\"content\"", false),
        new LocalProcessWorkspaceTool("capture_snapshot", "Capture current observed source and inputs after edits. Retain the returned snapshot identity and digest for an explicit corrected run.",
            $"{Project},{Revision},{Field("sourceDigest", Digest)}", "\"projectId\",\"expectedRevision\",\"sourceDigest\"", false),
        new LocalProcessWorkspaceTool("get_snapshot", "Read an explicitly retained snapshot under the current workspace grant.", Field("snapshotId", ProcessId), "\"snapshotId\"", true),
        new LocalProcessWorkspaceTool("read_snapshot_source", "Inspect source from a specific retained snapshot, including a corrected run; it must belong to this workspace.",
            $"{Field("snapshotId", ProcessId)},{Field("path", Path)}", "\"snapshotId\",\"path\"", true),
        new LocalProcessWorkspaceTool("list_runs", "Discover up to 32 recent local runs in this workspace, including runs started in Studio. Use actual returned run ids to inspect diagnostics.", "", "", true),
        new LocalProcessWorkspaceTool("run", "Run exact retained source and inputs in the configured isolated sandbox. Persist commandId before calling; after a lost response use get_command before retrying. A local run is not a published workflow service.",
            $"{Project},{Field("snapshotId", ProcessId)},{Field("snapshotDigest", Digest)},\"commandId\":{{\"type\":\"string\",\"pattern\":\"^[A-Za-z0-9-]{{1,80}}$\"}}",
            "\"projectId\",\"snapshotId\",\"snapshotDigest\",\"commandId\"", false),
        new LocalProcessWorkspaceTool("get_run", "Read actual local execution status, bounded diagnostics and verified output references.", Field("runId", ProcessId), "\"runId\"", true),
        new LocalProcessWorkspaceTool("get_command", "Reconcile a durable local run command after an uncertain response. Unknown acceptance never authorizes a fresh execution.", Field("commandId", Id), "\"commandId\"", true),
        new LocalProcessWorkspaceTool("cancel", "Request cancellation of an actual local run and retain the canonical terminal result.", Field("runId", ProcessId), "\"runId\"", false),
        new LocalProcessWorkspaceTool("read_artifact", "Read actual verified UTF-8 output from a successful local run (maximum 4 MiB). For GeoJSON, pass these bytes to honua_ingest_dataset, then use its actual connection/table receipt to publish and map. This tool does not import or publish.",
            $"{Field("runId", ProcessId)},{Field("path", Path)}", "\"runId\",\"path\"", true),
    ];

    public string Name => $"honua_workspace_local_process_{action}";
    public string WorkflowFamily => "geoprocessing";

    public McpToolDescriptor Describe() => new()
    {
        Name = Name,
        Description = description,
        InputSchema = Json($"{{\"type\":\"object\",\"properties\":{{{Field("workspaceId", Id)},{Field("turnId", Id)}{(fields.Length > 0 ? "," + fields : "")}}},\"required\":[\"workspaceId\",\"turnId\"{(required.Length > 0 ? "," + required : "")}],\"additionalProperties\":false}}"),
        Annotations = new() { ReadOnlyHint = readOnly, DestructiveHint = action == "cancel", IdempotentHint = action != "capture_snapshot" },
        Meta = Json("""{"honua.studio":{"family":"honua.workspace.local-process","view":"processes","revision":"1"}}"""),
    };

    public async Task<McpToolsCallResult> InvokeAsync(HttpContext httpContext, JsonElement? arguments, CancellationToken cancellationToken)
    {
        if (arguments is not { ValueKind: JsonValueKind.Object } args || args.GetRawText().Length > 1024 * 1024)
            return Error("A bounded local-process request is required.");
        try
        {
            var workspaceId = String(args, "workspaceId");
            var turnId = String(args, "turnId");
            var access = await LocalProcessWorkspaceAuthorization.AuthorizeAsync(httpContext, workspaceId, turnId, readOnly).ConfigureAwait(false);
            if (!access.Delegated) return Error("Use the paired scoped agent credential. Local project binding is a separate human action.");
            var gateway = httpContext.RequestServices.GetRequiredService<ILocalProcessWorkspaceGateway>();
            object result;
            switch (action)
            {
                case "get_project":
                    var project = await gateway.GetProjectAsync(access, cancellationToken).ConfigureAwait(false);
                    result = new LocalProcessAgentProject(project.ProjectId, project.Revision, project.SourceDigest, project.Image,
                        project.Files.Select(file => new LocalProcessSourceReference(file.Path, file.Sha256)).ToArray());
                    break;
                case "read_source":
                    if (args.TryGetProperty("current", out var current) && current.GetBoolean())
                        result = await gateway.ReadSourceAsync(access, String(args, "path"), cancellationToken).ConfigureAwait(false);
                    else
                    {
                        var document = await httpContext.RequestServices.GetRequiredService<IAgentWorkspaceStore>().GetAsync(workspaceId).ConfigureAwait(false);
                        var snapshot = document!.Turns[turnId].LocalProcessSnapshot!;
                        result = await gateway.ReadSnapshotSourceAsync(access, snapshot.SnapshotId, String(args, "path"), cancellationToken).ConfigureAwait(false);
                    }
                    break;
                case "write_source":
                    result = await gateway.WriteSourceAsync(access, String(args, "path"), String(args, "expectedSha256"),
                        new(String(args, "projectId"), args.GetProperty("expectedRevision").GetInt64(), String(args, "content")), cancellationToken).ConfigureAwait(false);
                    break;
                case "capture_snapshot":
                    result = await gateway.SnapshotAsync(access,
                        new(String(args, "projectId"), args.GetProperty("expectedRevision").GetInt64(), String(args, "sourceDigest")), cancellationToken).ConfigureAwait(false);
                    break;
                case "get_snapshot":
                    result = await gateway.GetSnapshotAsync(access, String(args, "snapshotId"), cancellationToken).ConfigureAwait(false);
                    break;
                case "read_snapshot_source":
                    result = await gateway.ReadSnapshotSourceAsync(access, String(args, "snapshotId"), String(args, "path"), cancellationToken).ConfigureAwait(false);
                    break;
                case "list_runs":
                    result = await gateway.ListRunsAsync(access, cancellationToken).ConfigureAwait(false);
                    break;
                case "run":
                    result = await gateway.RunAsync(access, new(String(args, "projectId"), String(args, "snapshotId"),
                        String(args, "snapshotDigest"), String(args, "commandId")), cancellationToken).ConfigureAwait(false);
                    break;
                case "get_run":
                    result = await gateway.GetRunAsync(access, String(args, "runId"), cancellationToken).ConfigureAwait(false);
                    break;
                case "get_command":
                    result = await gateway.GetCommandAsync(access, String(args, "commandId"), cancellationToken).ConfigureAwait(false);
                    break;
                case "cancel":
                    result = await gateway.CancelAsync(access, String(args, "runId"), cancellationToken).ConfigureAwait(false);
                    break;
                case "read_artifact":
                    var artifact = await gateway.ReadArtifactAsync(access, String(args, "runId"), String(args, "path"), cancellationToken).ConfigureAwait(false);
                    result = new LocalProcessTextArtifact(artifact.Reference, new UTF8Encoding(false, true).GetString(artifact.Content));
                    break;
                default: return Error("Unknown local-process action.");
            }
            var type = LocalProcessJsonContext.Default.GetTypeInfo(result.GetType())!;
            var content = JsonSerializer.SerializeToElement(result, type);
            return new() { StructuredContent = content, Content = [new() { Text = content.GetRawText() }] };
        }
        catch (LocalProcessWorkspaceException exception) { return Error(exception.Message); }
        catch (Exception exception) when (exception is ArgumentException or JsonException or KeyNotFoundException or InvalidOperationException or
            AgentWorkspaceConflictException or AgentWorkspaceUnavailableException or StackExchange.Redis.RedisException)
        {
            return Error("Local process request unavailable, invalid or unauthorized. Read current authorized state; reconcile existing commands before retrying mutations.");
        }
    }

    private static string String(JsonElement args, string key) => args.GetProperty(key).GetString() ?? throw new ArgumentException("Missing argument.");
    private static JsonElement Json(string value) { using var document = JsonDocument.Parse(value); return document.RootElement.Clone(); }
    private static McpToolsCallResult Error(string message) => new() { IsError = true, Content = [new() { Text = message }] };
}

internal sealed record LocalProcessSourceReference(string Path, string Sha256);
internal sealed record LocalProcessAgentProject(string ProjectId, long Revision, string SourceDigest, string Image, LocalProcessSourceReference[] Files);
internal sealed record LocalProcessTextArtifact(LocalProcessArtifactReference Reference, string Content);

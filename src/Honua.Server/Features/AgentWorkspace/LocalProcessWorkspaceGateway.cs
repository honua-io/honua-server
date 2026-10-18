// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Honua.Core.Features.AgentWorkspace.Abstractions;
using Honua.Core.Features.AgentWorkspace.Domain;

namespace Honua.Server.Features.AgentWorkspace;

/// <summary>Trusted transport to the host-selected sandbox adapter; never executes model-supplied commands or URLs.</summary>
internal sealed class LocalProcessWorkspaceGateway : ILocalProcessWorkspaceGateway
{
    private const int JsonBudget = 16 * 1024 * 1024;
    private const int ArtifactBudget = 4 * 1024 * 1024;
    private readonly HttpClient _client;
    private readonly Uri? _endpoint;
    private readonly string? _key;

    public LocalProcessWorkspaceGateway(HttpClient client, IConfiguration configuration)
    {
        _client = client;
        var url = configuration["LocalProcesses:WorkspaceUrl"];
        _key = configuration["HONUA_GP_WORKSPACE_KEY"];
        if (string.IsNullOrEmpty(url) && string.IsNullOrEmpty(_key)) return;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var endpoint) || endpoint.Scheme is not ("http" or "https") ||
            endpoint.UserInfo.Length != 0 || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0 ||
            endpoint.AbsolutePath != "/" || !PrivateHost(endpoint.DnsSafeHost) ||
            _key is not { Length: >= 32 and <= 512 } || _key.Any(character => character is < '!' or > '~'))
            throw new ArgumentException("Local processes require a pinned private origin and independent service key.");
        _endpoint = endpoint;
    }

    public bool IsConfigured => _endpoint is not null;

    public Task<LocalProcessProjectIdentity> GetIdentityAsync(LocalProcessWorkspaceAccess access, CancellationToken cancellationToken)
        => Get(access, "project/identity", LocalProcessJsonContext.Default.LocalProcessProjectIdentity, cancellationToken);

    public Task<LocalProcessProjectIdentity> BindAsync(LocalProcessWorkspaceAccess access, string projectId, CancellationToken cancellationToken)
    {
        RequireHuman(access);
        return Send(access, HttpMethod.Post, "project/bind", new LocalProcessBindRequest(projectId),
            LocalProcessJsonContext.Default.LocalProcessBindRequest, LocalProcessJsonContext.Default.LocalProcessProjectIdentity, cancellationToken);
    }

    public async Task RevokeAsync(LocalProcessWorkspaceAccess access, CancellationToken cancellationToken)
    {
        RequireHuman(access);
        using var request = Request(access, HttpMethod.Delete, "project/bindings/" + Identifier(access.WorkspaceId));
        _ = await SendBytes(request, JsonBudget, cancellationToken).ConfigureAwait(false);
    }

    public async Task<LocalProcessProject> GetProjectAsync(LocalProcessWorkspaceAccess access, CancellationToken cancellationToken)
    {
        var project = await Get(access, "project", LocalProcessJsonContext.Default.LocalProcessProject, cancellationToken).ConfigureAwait(false);
        return access.Delegated ? project with { IdeHandoff = null } : project;
    }

    public Task<LocalProcessSourceFile> ReadSourceAsync(LocalProcessWorkspaceAccess access, string path, CancellationToken cancellationToken)
        => Get(access, "sources/" + RelativePath(path), LocalProcessJsonContext.Default.LocalProcessSourceFile, cancellationToken);

    public Task<LocalProcessSourceWriteReceipt> WriteSourceAsync(LocalProcessWorkspaceAccess access, string path,
        string expectedSha256, LocalProcessSourceWrite request, CancellationToken cancellationToken)
        => Send(access, HttpMethod.Put, "sources/" + RelativePath(path), request, LocalProcessJsonContext.Default.LocalProcessSourceWrite,
            LocalProcessJsonContext.Default.LocalProcessSourceWriteReceipt, cancellationToken, Hex(expectedSha256, 64));

    public Task<LocalProcessSnapshot> SnapshotAsync(LocalProcessWorkspaceAccess access, LocalProcessSnapshotRequest request, CancellationToken cancellationToken)
        => Send(access, HttpMethod.Post, "snapshots", request, LocalProcessJsonContext.Default.LocalProcessSnapshotRequest,
            LocalProcessJsonContext.Default.LocalProcessSnapshot, cancellationToken);

    public Task<LocalProcessSnapshot> GetSnapshotAsync(LocalProcessWorkspaceAccess access, string snapshotId, CancellationToken cancellationToken)
        => Get(access, "snapshots/" + Hex(snapshotId, 32), LocalProcessJsonContext.Default.LocalProcessSnapshot, cancellationToken);

    public Task<LocalProcessSourceFile> ReadSnapshotSourceAsync(LocalProcessWorkspaceAccess access, string snapshotId, string path, CancellationToken cancellationToken)
        => Get(access, "snapshots/" + Hex(snapshotId, 32) + "/sources/" + RelativePath(path),
            LocalProcessJsonContext.Default.LocalProcessSourceFile, cancellationToken);

    public Task<LocalProcessRun> RunAsync(LocalProcessWorkspaceAccess access, LocalProcessRunRequest request, CancellationToken cancellationToken)
    {
        _ = CommandIdentifier(request.CommandId);
        return Send(access, HttpMethod.Post, "runs", request, LocalProcessJsonContext.Default.LocalProcessRunRequest,
            LocalProcessJsonContext.Default.LocalProcessRun, cancellationToken);
    }

    public Task<LocalProcessRun> GetRunAsync(LocalProcessWorkspaceAccess access, string runId, CancellationToken cancellationToken)
        => Get(access, "runs/" + Hex(runId, 32), LocalProcessJsonContext.Default.LocalProcessRun, cancellationToken);

    public Task<LocalProcessRunList> ListRunsAsync(LocalProcessWorkspaceAccess access, CancellationToken cancellationToken)
        => Get(access, "runs", LocalProcessJsonContext.Default.LocalProcessRunList, cancellationToken);

    public Task<LocalProcessRun> GetCommandAsync(LocalProcessWorkspaceAccess access, string commandId, CancellationToken cancellationToken)
        => Get(access, "commands/" + CommandIdentifier(commandId), LocalProcessJsonContext.Default.LocalProcessRun, cancellationToken);

    public Task<LocalProcessRun> CancelAsync(LocalProcessWorkspaceAccess access, string runId, CancellationToken cancellationToken)
        => Get(access, "runs/" + Hex(runId, 32) + "/cancel", LocalProcessJsonContext.Default.LocalProcessRun, cancellationToken, HttpMethod.Post);

    public async Task<LocalProcessArtifact> ReadArtifactAsync(LocalProcessWorkspaceAccess access, string runId, string path, CancellationToken cancellationToken)
    {
        var relative = RelativePath(path);
        var run = await GetRunAsync(access, runId, cancellationToken).ConfigureAwait(false);
        var matches = run.Artifacts.Where(item => item.Path == path).Take(2).ToArray();
        if (run.RunId != runId || run.Status != "succeeded" || matches.Length != 1 ||
            matches[0].Bytes is < 0 or > ArtifactBudget || !IsHex(matches[0].Sha256, 64))
            throw new LocalProcessWorkspaceException(409, "The run has no bounded successful artifact at this path.");
        var artifact = matches[0];
        using var request = Request(access, HttpMethod.Get, "runs/" + Hex(runId, 32) + "/artifacts/" + relative);
        var bytes = await SendBytes(request, (int)artifact.Bytes, cancellationToken).ConfigureAwait(false);
        if (bytes.LongLength != artifact.Bytes || !string.Equals(Convert.ToHexStringLower(SHA256.HashData(bytes)), artifact.Sha256, StringComparison.Ordinal))
            throw new LocalProcessWorkspaceException(502, "The output no longer matches its canonical artifact receipt.");
        return new(artifact, bytes);
    }

    private async Task<T> Get<T>(LocalProcessWorkspaceAccess access, string path, JsonTypeInfo<T> type,
        CancellationToken cancellationToken, HttpMethod? method = null)
    {
        using var request = Request(access, method ?? HttpMethod.Get, path);
        return Deserialize(await SendBytes(request, JsonBudget, cancellationToken).ConfigureAwait(false), type);
    }

    private async Task<TResponse> Send<TRequest, TResponse>(LocalProcessWorkspaceAccess access, HttpMethod method, string path,
        TRequest value, JsonTypeInfo<TRequest> requestType, JsonTypeInfo<TResponse> responseType,
        CancellationToken cancellationToken, string? ifMatch = null)
    {
        using var request = Request(access, method, path);
        request.Content = JsonContent.Create(value, requestType);
        if (ifMatch is not null) request.Headers.TryAddWithoutValidation("If-Match", '"' + ifMatch + '"');
        return Deserialize(await SendBytes(request, JsonBudget, cancellationToken).ConfigureAwait(false), responseType);
    }

    private HttpRequestMessage Request(LocalProcessWorkspaceAccess access, HttpMethod method, string path)
    {
        if (_endpoint is null) throw new LocalProcessWorkspaceException(503, "No local process workspace is configured.");
        var request = new HttpRequestMessage(method, new Uri(_endpoint, path));
        try
        {
            Add("X-Honua-Workspace-Key", _key!);
            Add("X-Honua-Actor-Id", access.ActorId);
            Add("X-Honua-Workspace-Owner-Id", access.OwnerActorId);
            Add("X-Honua-Workspace-Id", access.WorkspaceId);
            Add("X-Honua-Environment-Id", access.TargetId);
            if (access.TenantId is not null) Add("X-Honua-Tenant-Id", access.TenantId);
            Add("X-Honua-Workspace-Delegated", access.Delegated ? "true" : "false");
            return request;
        }
        catch
        {
            request.Dispose();
            throw;
        }

        void Add(string name, string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > 1024 || value != value.Trim() || value.Any(char.IsControl))
                throw new ArgumentException("Invalid trusted workspace assertion.");
            request.Headers.Add(name, value);
        }
    }

    private async Task<byte[]> SendBytes(HttpRequestMessage request, int budget, CancellationToken cancellationToken)
    {
        try
        {
            // Do not retry mutations here: resolve the durable command after an uncertain response.
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var status = (int)response.StatusCode;
                throw new LocalProcessWorkspaceException(status is 400 or 403 or 404 or 409 ? status : 502,
                    "Local process request refused. Read authorized state or reconcile the existing command before retrying.");
            }
            if (response.Content.Headers.ContentLength > budget)
                throw new LocalProcessWorkspaceException(502, "Local process response exceeded its byte limit.");
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var block = new byte[16384];
            while (true)
            {
                var count = await stream.ReadAsync(block, cancellationToken).ConfigureAwait(false);
                if (count == 0) break;
                if (buffer.Length + count > budget) throw new LocalProcessWorkspaceException(502, "Local process response exceeded its byte limit.");
                buffer.Write(block, 0, count);
            }
            return buffer.ToArray();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new LocalProcessWorkspaceException(504, "Local process response timed out. Reconcile the existing command before retrying.");
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException)
        {
            throw new LocalProcessWorkspaceException(503, "Local process transport is unavailable. Reconcile the existing command before retrying.");
        }
    }

    private static T Deserialize<T>(byte[] bytes, JsonTypeInfo<T> type)
    {
        try { return JsonSerializer.Deserialize(bytes, type) ?? throw new JsonException(); }
        catch (JsonException) { throw new LocalProcessWorkspaceException(502, "Local process returned an invalid receipt."); }
    }

    private static void RequireHuman(LocalProcessWorkspaceAccess access)
    {
        if (access.Delegated || access.ActorId != access.OwnerActorId)
            throw new LocalProcessWorkspaceException(403, "Only the authenticated human owner can bind or revoke a project.");
    }

    internal static string RelativePath(string path)
    {
        if (string.IsNullOrEmpty(path) || path.Length > 1024 || path.Any(character => char.IsControl(character) || character is '\\' or '%' or ':' or '?' or '#') ||
            path.Split('/').Any(segment => segment is "" or "." or "..")) throw new ArgumentException("A bounded project-relative path is required.");
        return string.Join('/', path.Split('/').Select(Uri.EscapeDataString));
    }

    private static string Identifier(string value) => AgentWorkspaceState.IsIdentifier(value) ? value : throw new ArgumentException("Invalid workspace identifier.");
    private static string CommandIdentifier(string value) => value is { Length: >= 1 and <= 80 } &&
        value.All(character => character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-')
            ? value : throw new ArgumentException("Invalid local-process command identifier.");
    private static bool IsHex(string value, int length) => value is not null && value.Length == length && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static string Hex(string value, int length) => IsHex(value, length) ? value : throw new ArgumentException("Invalid process identity or digest.");

    private static bool PrivateHost(string host)
    {
        if (host is "localhost" or "host.docker.internal") return true;
        if (!IPAddress.TryParse(host, out var address)) return false;
        if (IPAddress.IsLoopback(address)) return true;
        var bytes = address.GetAddressBytes();
        return bytes.Length == 4
            ? bytes[0] == 10 || bytes[0] == 172 && bytes[1] is >= 16 and <= 31 || bytes[0] == 192 && bytes[1] == 168
            : (bytes[0] & 0xfe) == 0xfc;
    }
}

internal sealed class LocalProcessWorkspaceException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}

internal sealed record LocalProcessBindRequest(string ProjectId);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(LocalProcessBindRequest))]
[JsonSerializable(typeof(LocalProcessProjectIdentity))]
[JsonSerializable(typeof(LocalProcessProject))]
[JsonSerializable(typeof(LocalProcessSourceFile))]
[JsonSerializable(typeof(LocalProcessSourceWrite))]
[JsonSerializable(typeof(LocalProcessSourceWriteReceipt))]
[JsonSerializable(typeof(LocalProcessSnapshotRequest))]
[JsonSerializable(typeof(LocalProcessSnapshot))]
[JsonSerializable(typeof(LocalProcessRunRequest))]
[JsonSerializable(typeof(LocalProcessRun))]
[JsonSerializable(typeof(LocalProcessRunList))]
[JsonSerializable(typeof(LocalProcessAgentProject))]
[JsonSerializable(typeof(LocalProcessTextArtifact))]
internal sealed partial class LocalProcessJsonContext : JsonSerializerContext;

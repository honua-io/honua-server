// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Honua.ControlPlane;

/// <summary>
/// One newline-delimited request on the Compose executor socket. There is no project, command, or
/// shell field: those concerns belong to operator configuration, and an extra JSON member is rejected.
/// </summary>
internal sealed record ComposeExecutorRequest
{
    /// <summary><c>status</c>, <c>converge</c>, <c>rollback</c>, or <c>restart</c>.</summary>
    public string? Op { get; init; }

    /// <summary>Immutable image reference for converge or rollback.</summary>
    public string? Image { get; init; }

    /// <summary>Optional backup id. When omitted, rollback selects the newest verifying manifest.</summary>
    public string? BackupId { get; init; }

    /// <summary>Exact migration approval token sealed in the approved proposal.</summary>
    public string? ContractMigrationApprovalNonce { get; init; }

    /// <summary>Caller correlation id copied onto the audit line.</summary>
    public string? RequestId { get; init; }
}

/// <summary>One newline-delimited response. Success requires <see cref="Ok"/> and <see cref="ReadbackComplete"/>.</summary>
internal sealed record ComposeExecutorResponse
{
    /// <summary>Caller correlation id, when the request carried one.</summary>
    public string? RequestId { get; init; }

    /// <summary>Whether the operation completed and readback agreed.</summary>
    public bool Ok { get; init; }

    /// <summary>Stable <see cref="ComposeExecutorCodes"/> value when <see cref="Ok"/> is false.</summary>
    public string? Code { get; init; }

    /// <summary>Short operator-facing detail. Never a customer body or a secret.</summary>
    public string? Message { get; init; }

    /// <summary>
    /// Requested image reference when the running container's image id matches that reference,
    /// otherwise the running image id.
    /// </summary>
    public string? RunningImage { get; init; }

    /// <summary>Whether image, schema, compatibility, health, and customer readback all completed.</summary>
    public bool ReadbackComplete { get; init; }

    /// <summary>Journal schema version observed in Postgres, when the query succeeded.</summary>
    public int? SchemaVersion { get; init; }

    /// <summary>Customer probe body, capped, and only when that probe succeeded.</summary>
    public string? CustomerBody { get; init; }

    /// <summary>Backup id written by converge or consumed by rollback.</summary>
    public string? BackupId { get; init; }
}

/// <summary>Manifest stored beside <c>database.dump</c>. The image is the inspected image id.</summary>
internal sealed record ComposeBackupManifest
{
    /// <summary>Backup id, <c>backup-</c> plus 32 lowercase hex digits.</summary>
    public string? BackupId { get; init; }

    /// <summary>Compose project name from operator configuration, not from the request.</summary>
    public string? Project { get; init; }

    /// <summary>Compose service name from operator configuration.</summary>
    public string? Service { get; init; }

    /// <summary>Inspected image id of the service that was backed up.</summary>
    public string? Image { get; init; }

    /// <summary>Schema version observed before the backup.</summary>
    public int SchemaVersion { get; init; }

    /// <summary>Lowercase SHA-256 of <c>database.dump</c>.</summary>
    public string? Sha256 { get; init; }

    /// <summary>When the backup was written.</summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>Dump file name. Only <c>database.dump</c> is opened.</summary>
    public string? DumpFile { get; init; }
}

/// <summary>One JSON line in <c>audit.jsonl</c>. No secrets, probe URLs, or customer body.</summary>
internal sealed record ComposeAuditRecord
{
    /// <summary>UTC time of the audit line.</summary>
    public DateTimeOffset Time { get; init; }

    /// <summary>Operation name.</summary>
    public string? Op { get; init; }

    /// <summary>Caller correlation id.</summary>
    public string? RequestId { get; init; }

    /// <summary>Image digest the operation named.</summary>
    public string? Image { get; init; }

    /// <summary>Backup id, when one was involved.</summary>
    public string? BackupId { get; init; }

    /// <summary>Whether the operation reported success.</summary>
    public bool Ok { get; init; }

    /// <summary>Stable code when the operation did not succeed.</summary>
    public string? Code { get; init; }

    /// <summary>Schema version observed, when known.</summary>
    public int? SchemaVersion { get; init; }

    /// <summary>Registered Compose project.</summary>
    public string? Project { get; init; }
}

/// <summary>
/// Source-generated JSON for the socket, manifests, and audit lines. Extra members are rejected so a
/// model cannot smuggle a project or command. Reflection serialization is disabled on this host.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(ComposeExecutorRequest))]
[JsonSerializable(typeof(ComposeExecutorResponse))]
[JsonSerializable(typeof(ComposeBackupManifest))]
[JsonSerializable(typeof(ComposeAuditRecord))]
internal sealed partial class ComposeExecutorJsonContext : JsonSerializerContext
{
}

/// <summary>Parses one socket request line into a typed request or a fail-closed response.</summary>
internal static class ComposeExecutorProtocol
{
    internal const int MaxRequestBytes = 64 * 1024;

    /// <summary>Dump file name. No other name is opened.</summary>
    internal const string DumpFileName = "database.dump";

    /// <summary>Override file the sidecar writes. Compose merges it over the operator's compose file.</summary>
    internal const string OverrideFileName = "compose.executor.image.yaml";

    /// <summary>
    /// Reads one JSON object. An extra member such as <c>project</c> or <c>command</c> is
    /// <see cref="ComposeExecutorCodes.ScopeViolation"/> and the caller must not invoke docker.
    /// Malformed JSON is <see cref="ComposeExecutorCodes.ConfigUnsupported"/>.
    /// </summary>
    internal static bool TryReadRequest(
        string line,
        out ComposeExecutorRequest? request,
        out ComposeExecutorResponse? failure)
    {
        request = null;
        failure = null;
        if (string.IsNullOrWhiteSpace(line))
        {
            failure = Refuse(null, ComposeExecutorCodes.ConfigUnsupported, "The executor request is empty.");
            return false;
        }

        try
        {
            request = JsonSerializer.Deserialize(line, ComposeExecutorJsonContext.Default.ComposeExecutorRequest);
            if (request == null || string.IsNullOrWhiteSpace(request.Op))
            {
                failure = Refuse(request?.RequestId, ComposeExecutorCodes.ConfigUnsupported, "The executor request has no op.");
                request = null;
                return false;
            }

            return true;
        }
        catch (JsonException ex)
        {
            var unmapped = ex.Message.Contains("could not be mapped", StringComparison.Ordinal)
                || ex.Message.Contains("unmapped", StringComparison.OrdinalIgnoreCase);
            failure = Refuse(
                null,
                unmapped ? ComposeExecutorCodes.ScopeViolation : ComposeExecutorCodes.ConfigUnsupported,
                unmapped
                    ? "The executor request named a field outside the allowlist."
                    : "The executor request is not valid JSON.");
            return false;
        }
    }

    internal static string Serialize(ComposeExecutorResponse response)
        => JsonSerializer.Serialize(response, ComposeExecutorJsonContext.Default.ComposeExecutorResponse);

    internal static ComposeExecutorResponse Refuse(string? requestId, string code, string message)
        => new()
        {
            RequestId = requestId,
            Ok = false,
            Code = code,
            Message = message,
            ReadbackComplete = false
        };
}

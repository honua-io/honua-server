// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Honua.ControlPlane;

internal sealed record ComposeExecutorRequest
{
    public string? Op { get; init; }

    public string? Service { get; init; }

    public string? Image { get; init; }

    public string? BackupId { get; init; }

    public string? RequestId { get; init; }

    public bool RequireAudit { get; init; }
}

internal sealed record ComposeExecutorResponse
{
    public string? RequestId { get; init; }

    public bool Ok { get; init; }

    public string? Code { get; init; }

    // Operator-facing detail never includes a customer body or a secret.
    public string? Message { get; init; }

    public string? RunningImage { get; init; }

    public bool ReadbackComplete { get; init; }

    public int? SchemaVersion { get; init; }

    public string? CustomerBody { get; init; }

    public string? ImageId { get; init; }

    public string? ServerVersion { get; init; }

    public IReadOnlyList<ComposeServiceObservation> Services { get; init; } = [];

    public string? BackupId { get; init; }
}

internal sealed record ComposeServiceObservation
{
    public string? Incarnation { get; init; }

    public required string Service { get; init; }

    public required string Status { get; init; }

    public required string Description { get; init; }
}

internal sealed record ComposeBackupManifest
{
    public string? BackupId { get; init; }

    public string? Project { get; init; }

    public string? Service { get; init; }

    public string? Image { get; init; }

    public int SchemaVersion { get; init; }

    public string? Sha256 { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public string? DumpFile { get; init; }
}

// The audit line carries no secrets, probe URLs, or customer body.
internal sealed record ComposeAuditRecord
{
    public DateTimeOffset Time { get; init; }

    public string? Op { get; init; }

    public string? RequestId { get; init; }

    public string? Image { get; init; }

    public string? BackupId { get; init; }

    public bool Ok { get; init; }

    public string? Code { get; init; }

    public int? SchemaVersion { get; init; }

    public string? Project { get; init; }
}

// Extra JSON members are rejected so a model cannot smuggle a project or a command.
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

internal static class ComposeExecutorProtocol
{
    internal const int MaxRequestBytes = 64 * 1024;

    internal const string DumpFileName = "database.dump";

    internal const string OverrideFileName = "compose.executor.image.yaml";

    // An extra member such as project or command is refused, and the caller must not invoke docker.
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

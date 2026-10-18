// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Honua.Core.Features.Import.Domain;

namespace Honua.Server.Features.AgentWorkspace;

internal sealed record DatasetImportTaskIntent(string ActorId, string IntentId, string RequestHash, string TaskId, long Revision);

internal static class DatasetImportTaskState
{
    internal static string IntentKey(string actorId, string intentId)
        => Digest(JsonSerializer.Serialize(new[] { actorId, intentId }, AgentWorkspaceJsonContext.Default.StringArray));

    internal static bool ValidAuthorityScope(string? workspaceId, string? taskId, long revision, string? contentHash)
        => AgentWorkspaceState.IsIdentifier(workspaceId) && AgentWorkspaceState.IsIdentifier(taskId) && revision > 0 && Hash(contentHash);

    internal static bool ValidRenewal(RenewDatasetImportAuthority? request)
        => request is not null && AgentWorkspaceState.IsIdentifier(request.RenewalId) &&
            request.ExpectedAuthorityRevision > 0 && Hash(request.TaskContentHash);

    internal static string RenewalKey(string taskId, long revision, string contentHash, string renewalId)
        => Digest(JsonSerializer.Serialize(new[] { taskId, revision.ToString(System.Globalization.CultureInfo.InvariantCulture), contentHash, renewalId },
            AgentWorkspaceJsonContext.Default.StringArray));

    internal static string ContentHash(DatasetImportTask task)
        => Digest(JsonSerializer.Serialize(task with { ContentHash = string.Empty }, AgentWorkspaceJsonContext.Default.DatasetImportTask));

    internal static string RequestHash(CreateDatasetImportTask request)
        => Digest(JsonSerializer.Serialize(request, AgentWorkspaceJsonContext.Default.CreateDatasetImportTask));

    internal static void Validate(CreateDatasetImportTask request)
    {
        if (!AgentWorkspaceState.IsIdentifier(request.IntentId) || !AgentWorkspaceState.IsIdentifier(request.ViewId) || request.ContextRevision < 0)
            throw new ArgumentException("A valid original intent and context binding are required.");
        var destination = request.Destination;
        if (destination is null || !Identifier(destination.Schema, true) || !Identifier(destination.Table, false))
            throw new ArgumentException("An explicit operational schema and table are required.");
        var source = request.Source;
        if (source is null || source.Kind is not ("browser-file" or "local-process-artifact") || source.TotalBytes <= 0 ||
            source.FileName is not { Length: > 0 and <= 255 } || source.FileName.Any(c => char.IsControl(c) || c is '/' or '\\') ||
            source.FileName != source.FileName.Trim() || source.ExpectedSha256 is not null && !Hash(source.ExpectedSha256))
            throw new ArgumentException("A bounded immutable source declaration is required.");
        if (source.Kind == "browser-file" && source.Artifact is not null || source.Kind == "local-process-artifact" &&
            (source.Artifact is not { } artifact || !AgentWorkspaceState.IsIdentifier(artifact.ProjectId) ||
             !AgentWorkspaceState.IsIdentifier(artifact.SnapshotId) || !AgentWorkspaceState.IsIdentifier(artifact.RunId) ||
             !Hash(artifact.SnapshotDigest) || !Hash(source.ExpectedSha256) || !ArtifactPath(artifact.Path)))
            throw new ArgumentException("Canonical local process artifact provenance is required.");
        if (request.Options is not { } options || options.LoadMode is not (ImportLoadMode.Append or ImportLoadMode.Replace) ||
            options.TargetSrid <= 0 || options.SourceSrid is <= 0)
            throw new ArgumentException("Unsupported import options. Upsert requires a canonical key-column contract.");
    }

    internal static bool Hash(string? value) => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static bool Identifier(string? value, bool underscoreFirst) => value is { Length: > 0 and <= 63 } &&
        (char.IsAsciiLetter(value[0]) || underscoreFirst && value[0] == '_') && value.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');
    private static bool ArtifactPath(string? path) => path is { Length: > 0 and <= 1024 } && !path.Any(char.IsControl) &&
        !path.Contains('\\') && !path.StartsWith('/') && path.Split('/').All(segment => segment.Length > 0 && segment is not ("." or ".."));
    private static string Digest(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}

// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text;
using System.Text.Json;
using Honua.Core.Exceptions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;

namespace Honua.Server.Features.Admin;

internal sealed record ServiceFolderAssignment(string ServiceId, string? PriorFolderId);

internal sealed record ServiceMoveEvidenceRecord(
    string ServiceName,
    string PostconditionEtag,
    IReadOnlyList<ServiceFolderAssignment> Assignments)
{
    internal string? Namespace { get; init; }
    internal string? CompensationInstanceId { get; init; }
}

internal enum ServiceMoveRecoveryKind
{
    Ready,
    AlreadyRestored,
    CatalogChanged,
    NotRestorable
}

internal static class ServiceMoveEvidence
{
    internal const string SupportedReason =
        "Restore the recorded prior folder through the operation handle while the catalog ETag is unchanged.";

    internal const string AssignmentsKey = "serviceMove.priorAssignments";
    internal const string EtagKey = "serviceMove.postconditionEtag";
    internal const string ServiceNameKey = "serviceMove.serviceName";
    internal const string NamespaceKey = "serviceMove.namespace";
    internal const string CompensationKey = "serviceMove.compensationInstanceId";

    internal static void Write(IDictionary<string, string> details, ServiceMoveEvidenceRecord evidence)
    {
        details[ServiceNameKey] = evidence.ServiceName;
        details[EtagKey] = evidence.PostconditionEtag;
        details[AssignmentsKey] = WriteAssignments(evidence.Assignments);
        if (!string.IsNullOrWhiteSpace(evidence.Namespace))
            details[NamespaceKey] = evidence.Namespace;
    }

    internal static ServiceMoveEvidenceRecord? Read(IReadOnlyDictionary<string, string> details)
    {
        if (!details.TryGetValue(ServiceNameKey, out var serviceName) || string.IsNullOrWhiteSpace(serviceName) ||
            !details.TryGetValue(EtagKey, out var etag) || string.IsNullOrWhiteSpace(etag) ||
            !details.TryGetValue(AssignmentsKey, out var assignmentsJson))
            return null;
        var assignments = ReadAssignments(assignmentsJson);
        if (assignments is null) return null;
        details.TryGetValue(NamespaceKey, out var ns);
        details.TryGetValue(CompensationKey, out var compensationId);
        return new(serviceName, etag, assignments)
        {
            Namespace = string.IsNullOrWhiteSpace(ns) ? null : ns,
            CompensationInstanceId = string.IsNullOrWhiteSpace(compensationId) ? null : compensationId
        };
    }

    internal static ServiceMoveRecoveryKind Decide(MetadataV2Graph current, string currentEtag, ServiceMoveEvidenceRecord evidence)
    {
        if (evidence.Assignments.Count == 0) return ServiceMoveRecoveryKind.NotRestorable;
        if (IsAtPriorFolders(current, evidence)) return ServiceMoveRecoveryKind.AlreadyRestored;
        if (!string.Equals(currentEtag, evidence.PostconditionEtag, StringComparison.Ordinal))
            return ServiceMoveRecoveryKind.CatalogChanged;
        return ServicesMatch(current, evidence) ? ServiceMoveRecoveryKind.Ready : ServiceMoveRecoveryKind.NotRestorable;
    }

    internal static async Task PersistHttpMoveAsync(HttpContext context, FolderMutationCommand.Move command, FolderExecutionResult execution)
    {
        var saved = execution.Saved;
        var changed = execution.Mutation;
        if (saved is null || changed is null || changed.MovedServiceName is null || changed.PriorAssignments.Count == 0) return;
        var store = context.RequestServices.GetService<IOperationInstanceStore>();
        if (store is null) return;
        var cancellationToken = context.RequestAborted;
        var now = context.RequestServices.GetService<TimeProvider>()?.GetUtcNow() ?? TimeProvider.System.GetUtcNow();
        var correlation = context.Request.Headers["X-Correlation-ID"].ToString();
        var details = new Dictionary<string, string>(StringComparer.Ordinal);
        Write(details, new ServiceMoveEvidenceRecord(changed.MovedServiceName, saved.Etag, changed.PriorAssignments)
        {
            Namespace = execution.Namespace
        });
        var handle = new OperationHandle
        {
            OperationInstanceId = $"opinst-{Guid.NewGuid():N}",
            OperationId = command.OperationId,
            Status = OperationHandleStatus.Completed,
            CorrelationId = string.IsNullOrWhiteSpace(correlation) ? $"corr-{Guid.NewGuid():N}" : correlation,
            CreatedAt = now,
            UpdatedAt = now,
            TenantId = context.RequestServices.GetService<Honua.Core.Features.MultiTenancy.Abstractions.ITenantContext>()?.TenantId,
            Result = new OperationResultSummary
            {
                Summary = "Move service completed.",
                Details = details
            }
        };
        try
        {
            if (await store.TryCreateAsync(handle, cancellationToken).ConfigureAwait(false))
                context.Response.Headers["X-Honua-Operation-Instance-Id"] = handle.OperationInstanceId;
        }
        catch (CapabilityUnavailableException)
        {
            // The catalog move already committed; omit the compensation handle header when durable instance storage is unavailable.
        }
    }

    internal static OperationHandle WithCompensation(OperationHandle handle, string compensationInstanceId, DateTimeOffset updatedAt)
    {
        var details = new Dictionary<string, string>(handle.Result?.Details ?? new Dictionary<string, string>(), StringComparer.Ordinal)
        {
            [CompensationKey] = compensationInstanceId
        };
        return handle with
        {
            UpdatedAt = updatedAt,
            Result = new OperationResultSummary
            {
                Summary = handle.Result?.Summary ?? "Move service completed.",
                Details = details,
                SecretReferences = handle.Result?.SecretReferences ?? []
            }
        };
    }

    private static bool IsAtPriorFolders(MetadataV2Graph current, ServiceMoveEvidenceRecord evidence)
        => evidence.Assignments.All(assignment => current.Services.Any(service =>
            service.Metadata.Id == assignment.ServiceId &&
            string.Equals(service.Metadata.Name, evidence.ServiceName, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(service.FolderId, assignment.PriorFolderId, StringComparison.Ordinal)));

    private static bool ServicesMatch(MetadataV2Graph current, ServiceMoveEvidenceRecord evidence)
        => evidence.Assignments.All(assignment => current.Services.Any(service =>
            service.Metadata.Id == assignment.ServiceId &&
            string.Equals(service.Metadata.Name, evidence.ServiceName, StringComparison.OrdinalIgnoreCase)));

    private static string WriteAssignments(IReadOnlyList<ServiceFolderAssignment> assignments)
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();
            foreach (var assignment in assignments.OrderBy(item => item.ServiceId, StringComparer.Ordinal))
            {
                writer.WriteStartObject();
                writer.WriteString("serviceId", assignment.ServiceId);
                if (assignment.PriorFolderId is null) writer.WriteNull("priorFolderId");
                else writer.WriteString("priorFolderId", assignment.PriorFolderId);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static List<ServiceFolderAssignment>? ReadAssignments(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array) return null;
            var read = new List<ServiceFolderAssignment>();
            foreach (var item in document.RootElement.EnumerateArray())
            {
                var assignment = ReadAssignment(item);
                if (assignment is null) return null;
                read.Add(assignment);
            }
            if (read.Count == 0 || read.Select(item => item.ServiceId).Distinct(StringComparer.Ordinal).Count() != read.Count)
                return null;
            return read;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static ServiceFolderAssignment? ReadAssignment(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object) return null;
        if (!item.TryGetProperty("serviceId", out var serviceId) || serviceId.ValueKind != JsonValueKind.String)
            return null;
        if (!item.TryGetProperty("priorFolderId", out var folder)) return null;
        var prior = folder.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.String => folder.GetString(),
            _ => string.Empty
        };
        if (prior is { Length: 0 } || string.IsNullOrWhiteSpace(serviceId.GetString())) return null;
        return new ServiceFolderAssignment(serviceId.GetString()!, prior);
    }
}

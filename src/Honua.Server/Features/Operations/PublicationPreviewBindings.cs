// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Honua.Core.Features.Admin.Abstractions;
using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;

namespace Honua.Server.Features.Operations;

// Durable, actor-bound references. Reading a reference never extends its original lifetime.
internal sealed record PublicationPreviewIdentity(OperationPolicyContext Policy, LayerPublicationBase MetadataBase, string SourceFingerprint);

internal sealed class PublicationPreviewBindings(IOperationInstanceStore store, TimeProvider clock)
{
    internal const string ReferenceKey = "previewReference";
    private const string Prefix = "publication-preview-";
    private const string BindingOperationId = "internal.service.publication.preview.binding";
    internal static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);

    public async Task<(string Reference, DateTimeOffset ExpiresAt)> CreateAsync(
        OperationRequest request, PublicationPreviewIdentity identity, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(identity.Policy.PrincipalId))
        {
            throw InvalidReference();
        }
        var now = clock.GetUtcNow();
        var expires = now + Lifetime;
        var reference = Prefix + Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var envelope = new OperationHandle
        {
            OperationInstanceId = reference,
            OperationId = BindingOperationId,
            TenantId = identity.Policy.TenantId,
            CorrelationId = identity.Policy.CorrelationId ?? $"corr-{Guid.NewGuid():N}",
            Status = OperationHandleStatus.Completed,
            CreatedAt = now,
            UpdatedAt = now,
            Result = new OperationResultSummary
            {
                Summary = "Retained publication preview binding.",
                Details = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["requesterId"] = identity.Policy.PrincipalId,
                    ["requestHash"] = ComputeRequestHash(request),
                    ["sourceFingerprint"] = identity.SourceFingerprint,
                    ["metadataRevision"] = identity.MetadataBase.Revision.ToString(CultureInfo.InvariantCulture),
                    ["metadataEtag"] = identity.MetadataBase.Etag ?? string.Empty,
                    ["expiresAt"] = expires.ToString("O", CultureInfo.InvariantCulture)
                }
            }
        };
        if (!await store.TryCreateAsync(envelope, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("Publication preview could not be retained.");
        }
        return (reference, expires);
    }

    public async Task VerifyAsync(OperationRequest request, PublicationPreviewIdentity identity, CancellationToken cancellationToken)
    {
        if (!request.Parameters.TryGetValue(ReferenceKey, out var reference))
        {
            return;
        }
        if (reference is null || reference.Length != Prefix.Length + 64 ||
            !reference.StartsWith(Prefix, StringComparison.Ordinal) ||
            reference.Skip(Prefix.Length).Any(character => !char.IsAsciiHexDigit(character)))
        {
            throw InvalidReference();
        }
        var envelope = await store.GetAsync(reference, cancellationToken).ConfigureAwait(false);
        var details = envelope?.Result?.Details;
        if (envelope is null || envelope.OperationId != BindingOperationId ||
            envelope.Status != OperationHandleStatus.Completed || details is null ||
            !string.Equals(envelope.TenantId, identity.Policy.TenantId, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(identity.Policy.PrincipalId) ||
            !details.TryGetValue("requesterId", out var requester) || requester != identity.Policy.PrincipalId ||
            !details.TryGetValue("sourceFingerprint", out var sourceFingerprint) || sourceFingerprint != identity.SourceFingerprint ||
            !details.TryGetValue("requestHash", out var hash) || hash != ComputeRequestHash(request) ||
            !details.TryGetValue("expiresAt", out var expiresText) ||
            !DateTimeOffset.TryParseExact(expiresText, "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out var expires) ||
            clock.GetUtcNow() >= expires ||
            !details.TryGetValue("metadataRevision", out var revision) ||
            revision != identity.MetadataBase.Revision.ToString(CultureInfo.InvariantCulture) ||
            !details.TryGetValue("metadataEtag", out var etag) || etag != (identity.MetadataBase.Etag ?? string.Empty))
        {
            throw InvalidReference();
        }
    }

    private static InvalidOperationException InvalidReference()
        => new("Publication preview is unavailable or no longer matches. Create a new preview.");

    private static string ComputeRequestHash(OperationRequest request)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("connectionId", Guid.TryParse(request.ConnectionId, out var id) ? id.ToString("D") : request.ConnectionId);
            writer.WriteString("serviceName", request.ServiceName);
            writer.WriteStartArray("fields");
            foreach (var field in request.Fields)
            {
                writer.WriteStringValue(field);
            }
            writer.WriteEndArray();
            writer.WriteStartObject("parameters");
            foreach (var (key, value) in request.Parameters.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                if (key is ReferenceKey or ServicePublishMetadataPin.RevisionKey or ServicePublishMetadataPin.EtagKey or
                    "connectionId" or "serviceName" or "fields" or "connectionEndpointFingerprint" or VerifiedCommittedSinkPublication.PublisherActorIdKey or
                    VerifiedCommittedSinkPublication.PublisherWasAdministratorKey)
                {
                    continue;
                }
                if (value is not null || key == "folderId")
                {
                    writer.WriteString(key, value);
                }
            }
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        return Convert.ToHexString(SHA256.HashData(stream.ToArray()));
    }
}

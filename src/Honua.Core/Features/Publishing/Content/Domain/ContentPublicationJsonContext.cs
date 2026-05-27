// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Core.Features.Security.Domain;

namespace Honua.Core.Features.Publishing.Content.Domain;

/// <summary>
/// Source-generated JSON serialization context for the content publication registry.
/// Covers domain records, JSONB sidecars (policy, dependencies, provenance), HTTP
/// request DTOs, and read projections so the slice stays AOT/trimming-safe.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(ContentPublicationVersion))]
[JsonSerializable(typeof(ContentPublicationRouteState))]
[JsonSerializable(typeof(ContentPublicationEvent))]
[JsonSerializable(typeof(ContentPublicationDetail))]
[JsonSerializable(typeof(ContentPublicationPolicyUpdateResponse))]
[JsonSerializable(typeof(PublishedArtifactView))]
[JsonSerializable(typeof(ContentPublicationPolicy))]
[JsonSerializable(typeof(ContentSharePolicy))]
[JsonSerializable(typeof(ContentEmbedPolicy))]
[JsonSerializable(typeof(ContentServicePolicy))]
[JsonSerializable(typeof(ContentPublicLinkPolicy))]
[JsonSerializable(typeof(ContentPublicLink))]
[JsonSerializable(typeof(ContentPublicationBbox))]
[JsonSerializable(typeof(ContentPublicationDependencyRef))]
[JsonSerializable(typeof(ContentPublicationProvenanceRef))]
[JsonSerializable(typeof(AccessPolicy))]
[JsonSerializable(typeof(PublishContentRequest))]
[JsonSerializable(typeof(RepublishContentRequest))]
[JsonSerializable(typeof(RollbackContentRequest))]
[JsonSerializable(typeof(UpdatePublicationPolicyRequest))]
[JsonSerializable(typeof(ContentPublicLinkRequest))]
[JsonSerializable(typeof(ContentPublicationVersion[]))]
[JsonSerializable(typeof(ContentPublicationEvent[]))]
[JsonSerializable(typeof(ContentPublicationDependencyRef[]))]
[JsonSerializable(typeof(ContentPublicationProvenanceRef[]))]
[JsonSerializable(typeof(ContentPublicLink[]))]
[JsonSerializable(typeof(string[]))]
public sealed partial class ContentPublicationJsonContext : JsonSerializerContext
{
}

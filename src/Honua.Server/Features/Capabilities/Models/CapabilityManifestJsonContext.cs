// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Server.Features.Capabilities.Models;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(CapabilityManifestDocument))]
[JsonSerializable(typeof(CapabilityManifestScope))]
[JsonSerializable(typeof(CapabilityManifestServerInfo))]
[JsonSerializable(typeof(CapabilityManifestEnvironment))]
[JsonSerializable(typeof(CapabilityManifestPackages))]
[JsonSerializable(typeof(CapabilityManifestPackageFamily))]
[JsonSerializable(typeof(CapabilityManifestCapability))]
[JsonSerializable(typeof(CapabilityManifestTransports))]
[JsonSerializable(typeof(CapabilityManifestTransportState))]
[JsonSerializable(typeof(CapabilityManifestLimits))]
[JsonSerializable(typeof(CapabilityManifestPreviewLimits))]
[JsonSerializable(typeof(CapabilityManifestQueryLimits))]
[JsonSerializable(typeof(CapabilityManifestAnalysisLimits))]
[JsonSerializable(typeof(CapabilityManifestPublicationLimits))]
[JsonSerializable(typeof(CapabilityManifestJobLimits))]
[JsonSerializable(typeof(CapabilityManifestUploadLimits))]
[JsonSerializable(typeof(CapabilityManifestStreamingLimits))]
[JsonSerializable(typeof(CapabilityManifestEditLimits))]
[JsonSerializable(typeof(CapabilityManifestGeometryLimits))]
[JsonSerializable(typeof(CapabilityManifestAttachmentLimits))]
[JsonSerializable(typeof(CapabilityManifestPolicies))]
[JsonSerializable(typeof(CapabilityManifestEntitlementDecision))]
[JsonSerializable(typeof(CapabilityManifestLink))]
[JsonSerializable(typeof(CapabilityManifestPackageFamily[]))]
[JsonSerializable(typeof(CapabilityManifestCapability[]))]
[JsonSerializable(typeof(CapabilityManifestTransportState[]))]
[JsonSerializable(typeof(CapabilityManifestEntitlementDecision[]))]
[JsonSerializable(typeof(CapabilityManifestLink[]))]
[JsonSerializable(typeof(string[]))]
internal sealed partial class CapabilityManifestJsonContext : JsonSerializerContext
{
}

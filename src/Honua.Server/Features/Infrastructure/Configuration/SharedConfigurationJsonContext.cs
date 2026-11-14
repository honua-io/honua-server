// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Core.Features.Identity.Domain;
using Honua.Core.Features.Operations.Domain;
using Honua.Core.Features.RateLimiting.Domain;
using Honua.Infrastructure.Authentication.ClientCertificates;

namespace Honua.Server.Features.Infrastructure.Configuration;

[JsonSerializable(typeof(AgentAuthorityCeiling))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(OidcProviderConfiguration))]
[JsonSerializable(typeof(RateLimitPolicy))]
[JsonSerializable(typeof(ClientCertificateTrustProfile))]
[JsonSerializable(typeof(ConfiguredTrustManifest))]
internal sealed partial class SharedConfigurationJsonContext : JsonSerializerContext;

// Records which trust profiles and mappings were imported from configuration, so removing them from configuration revokes them.
internal sealed record ConfiguredTrustManifest
{
    public long Revision { get; init; }

    public Dictionary<string, string[]> Profiles { get; init; } = new(StringComparer.Ordinal);
}

// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Core.Features.Identity.Domain;
using Honua.Core.Features.RateLimiting.Domain;
using Honua.Infrastructure.Authentication.ClientCertificates;

namespace Honua.Server.Features.Infrastructure.Configuration;

[JsonSerializable(typeof(OidcProviderConfiguration))]
[JsonSerializable(typeof(RateLimitPolicy))]
[JsonSerializable(typeof(ClientCertificateTrustProfile))]
internal sealed partial class ConfigurationJsonContext : JsonSerializerContext;

// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Routing.Features.Routing.Domain;

namespace Honua.Geoprocessing.Execution;

/// <summary>Reflection-free durable serialization of canonical routing requests.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(RouteSolveRequest))]
[JsonSerializable(typeof(ServiceAreaSolveRequest))]
internal sealed partial class RoutingJobJsonContext : JsonSerializerContext;

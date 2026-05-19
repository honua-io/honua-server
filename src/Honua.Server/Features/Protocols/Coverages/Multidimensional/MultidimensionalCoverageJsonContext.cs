// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Server.Features.Protocols.Coverages.Multidimensional.Models;

namespace Honua.Server.Features.Protocols.Coverages.Multidimensional;

/// <summary>
/// AOT-safe JSON serialization context for the multidimensional coverage
/// admin surface.
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(RegisterMultidimensionalCoverageRequest))]
[JsonSerializable(typeof(MultidimensionalCoverageRegistrationResponse))]
[JsonSerializable(typeof(MultidimensionalCoverageRegistrationResponse[]))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(long))]
internal sealed partial class MultidimensionalCoverageJsonContext : JsonSerializerContext
{
}

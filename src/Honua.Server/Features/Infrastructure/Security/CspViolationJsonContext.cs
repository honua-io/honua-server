// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Infrastructure.Security;

/// <summary>
/// JSON serialization context for CSP violation reports with AOT support.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.KebabCaseLower,
    PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(CspViolationReport))]
[JsonSerializable(typeof(CspViolationDetails))]
internal sealed partial class CspViolationJsonContext : JsonSerializerContext
{
}

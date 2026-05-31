// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Core.Features.Validation.Contracts;

/// <summary>
/// Severity of a single field-level validation error. Modeled on the Studio
/// package diagnostic severity (the canonical, AOT/JSON-wired shape) so the
/// shared <see cref="FieldValidationError"/> contract serializes identically.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<ValidationSeverity>))]
public enum ValidationSeverity
{
    /// <summary>Informational diagnostic; does not block.</summary>
    [JsonStringEnumMemberName("info")]
    Info,

    /// <summary>Warning diagnostic; does not block but should be surfaced.</summary>
    [JsonStringEnumMemberName("warning")]
    Warning,

    /// <summary>Error diagnostic; blocks the validated subject.</summary>
    [JsonStringEnumMemberName("error")]
    Error,

    /// <summary>Blocking diagnostic; hard stop.</summary>
    [JsonStringEnumMemberName("blocker")]
    Blocker,
}

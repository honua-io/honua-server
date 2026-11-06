// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Core.Features.Validation.Contracts;

// Severity values match the Studio package diagnostic so the shared contract serializes the same shape.
[JsonConverter(typeof(JsonStringEnumConverter<ValidationSeverity>))]
public enum ValidationSeverity
{
    [JsonStringEnumMemberName("info")]
    Info,

    [JsonStringEnumMemberName("warning")]
    Warning,

    [JsonStringEnumMemberName("error")]
    Error,

    [JsonStringEnumMemberName("blocker")]
    Blocker,
}

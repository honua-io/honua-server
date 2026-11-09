// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;

namespace Honua.Core.Features.AuditLog;

[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)]
public sealed class AuditChainVerificationOptions
{
    public const string SectionName = "AuditLog:ChainVerification";

    public bool Enabled { get; set; } = true;

    public TimeSpan Interval { get; set; } = TimeSpan.FromHours(6);

    public TimeSpan InitialDelay { get; set; } = TimeSpan.FromMinutes(2);

    public string? Key { get; set; }
}

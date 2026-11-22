// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Diagnostics.CodeAnalysis;

namespace Honua.Core.Features.AuditLog;

// Operator switches that widen what the security audit trail records. Both are off by
// default because each one adds an event per ordinary request or edit.
[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)]
public sealed class AuditPolicyOptions
{
    public const string SectionName = "AuditLog:Policy";

    // Record every feature create and update, including a single-feature write, not only
    // deletes and multi-feature batches.
    public bool RecordSingleFeatureWrites { get; set; }

    // Record successful reads (GET/HEAD) made by an authenticated caller on any routed
    // endpoint. Anonymous reads stay unrecorded so unauthenticated traffic cannot drive
    // audit volume.
    public bool RecordSuccessfulReads { get; set; }
}

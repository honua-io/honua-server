// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Exceptions;

public sealed class CapabilityUnavailableException : InvalidOperationException
{
    public CapabilityUnavailableException(
        string detail,
        string missingDependency,
        string remediation,
        string remediationRef,
        string? capability = null)
        : base(detail)
    {
        MissingDependency = missingDependency;
        Remediation = remediation;
        RemediationRef = remediationRef;
        Capability = capability;
    }

    public string MissingDependency { get; }

    public string Remediation { get; }

    public string RemediationRef { get; }

    public string? Capability { get; }
}

// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Exceptions;

/// <summary>Signals that a request reached a deliberately uncomposed capability.</summary>
public sealed class CapabilityUnavailableException : InvalidOperationException
{
    public CapabilityUnavailableException(
        string detail,
        string missingDependency,
        string remediation,
        string remediationRef)
        : base(detail)
    {
        MissingDependency = missingDependency;
        Remediation = remediation;
        RemediationRef = remediationRef;
    }

    /// <summary>Machine-readable dependency identifier.</summary>
    public string MissingDependency { get; }

    /// <summary>Operator-facing remediation text.</summary>
    public string Remediation { get; }

    /// <summary>Canonical documentation reference for remediation.</summary>
    public string RemediationRef { get; }
}

// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Reporting.Domain;

/// <summary>
/// Base type for reporting-feature exceptions. Mirrors the geoprocessing
/// exception hierarchy so the server-side adapter can translate to either
/// ProblemDetails or MCP error envelopes consistently.
/// </summary>
public abstract class AnalysisReportException : Exception
{
    /// <summary>Creates an exception with the supplied message.</summary>
    protected AnalysisReportException(string message) : base(message) { }

    /// <summary>Creates an exception with the supplied message and inner cause.</summary>
    protected AnalysisReportException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// Raised by a renderer when it encounters a report contract version it does
/// not support.
/// </summary>
public sealed class UnsupportedReportContractVersionException : AnalysisReportException
{
    /// <summary>Stable error code for adapters and clients.</summary>
    public static string Code => ReportingConstants.UnsupportedContractVersionErrorCode;

    /// <summary>Contract version that was rejected.</summary>
    public string RequestedVersion { get; }

    /// <summary>Contract version the renderer accepts.</summary>
    public string SupportedVersion { get; }

    /// <summary>Creates a new exception describing the unsupported contract version.</summary>
    public UnsupportedReportContractVersionException(string requestedVersion, string supportedVersion)
        : base($"Report contract version '{requestedVersion}' is not supported by this renderer (supported: '{supportedVersion}').")
    {
        RequestedVersion = requestedVersion;
        SupportedVersion = supportedVersion;
    }
}

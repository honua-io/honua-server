// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Reporting.Domain;

public abstract class AnalysisReportException : Exception
{
    protected AnalysisReportException(string message) : base(message) { }

    protected AnalysisReportException(string message, Exception inner) : base(message, inner) { }
}

public sealed class UnsupportedReportContractVersionException : AnalysisReportException
{
    public static string Code => ReportingConstants.UnsupportedContractVersionErrorCode;

    public string RequestedVersion { get; }

    public string SupportedVersion { get; }

    public UnsupportedReportContractVersionException(string requestedVersion, string supportedVersion)
        : base($"Report contract version '{requestedVersion}' is not supported by this renderer (supported: '{supportedVersion}').")
    {
        RequestedVersion = requestedVersion;
        SupportedVersion = supportedVersion;
    }
}

// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Licensing.Domain;

namespace Honua.Infrastructure.Licensing;

internal static partial class LicenseCapacityMeterLog
{
    [LoggerMessage(
        EventId = 10020,
        Level = LogLevel.Warning,
        Message = "Serving instance registration refused by license capacity band. instanceId={InstanceId} currentUnits={CurrentUnits} joiningUnits={JoiningUnits} maxSustainedUnits={MaxSustainedUnits} state={State}")]
    public static partial void RegistrationRefused(
        ILogger logger,
        string instanceId,
        decimal currentUnits,
        decimal joiningUnits,
        decimal maxSustainedUnits,
        LicenseCapacityBandState state);

    [LoggerMessage(
        EventId = 10021,
        Level = LogLevel.Warning,
        Message = "Serving instance heartbeat refresh was not accepted. reason={Reason}")]
    public static partial void RefreshRefused(ILogger logger, string reason);

    [LoggerMessage(
        EventId = 10022,
        Level = LogLevel.Warning,
        Message = "License capacity meter entered fail-open metering gap after Redis error. errorType={ErrorType}")]
    public static partial void RedisMeteringGap(ILogger logger, string errorType);

    [LoggerMessage(
        EventId = 10024,
        Level = LogLevel.Error,
        Message = "Serving instance registration was refused by license capacity band at startup; the instance continues without a coordinated registration. reason={Reason}")]
    public static partial void RegistrationStartupRefused(ILogger logger, string reason);

    [LoggerMessage(
        EventId = 10023,
        Level = LogLevel.Information,
        Message = "License capacity surge mode changed. enabled={Enabled} reason={Reason}")]
    public static partial void SurgeModeChanged(ILogger logger, bool enabled, string? reason);
}

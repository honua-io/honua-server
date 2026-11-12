// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Capabilities;

public sealed class JobDurabilityOptions
{
    public const string SectionName = "Jobs";

    public bool RequireDurableStore { get; set; }
}

public sealed class DurableJobSubstrateUnavailableException : Exception
{
    public DurableJobSubstrateUnavailableException()
        : this(DurableJobSubstrateCause.SystemDatabaseNotConfigured)
    {
    }

    public DurableJobSubstrateUnavailableException(string message)
        : this(message, innerException: null)
    {
    }

    public DurableJobSubstrateUnavailableException(string message, Exception? innerException)
        : base(message, innerException)
    {
        Cause = DurableJobSubstrateCause.SystemDatabaseNotConfigured;
        Remediation = DurableJobSubstrateRemediation.For(Cause);
    }

    public DurableJobSubstrateUnavailableException(DurableJobSubstrateCause cause)
        : base(BuildMessage(cause))
    {
        Cause = cause;
        Remediation = DurableJobSubstrateRemediation.For(cause);
    }

    public DurableJobSubstrateCause Cause { get; }

    public string Remediation { get; }

    public const string CapabilityId = CapabilityUnavailableCodes.DurableJobsCapability;

    private static string BuildMessage(DurableJobSubstrateCause cause)
        => $"{JobDurabilityOptions.SectionName}:{nameof(JobDurabilityOptions.RequireDurableStore)} is enabled, "
            + $"but the durable job store is unavailable ({cause}). {DurableJobSubstrateRemediation.For(cause)} "
            + $"Alternatively, clear {JobDurabilityOptions.SectionName}:{nameof(JobDurabilityOptions.RequireDurableStore)} "
            + "to run without durable jobs.";
}

public static class DurableJobSubstrateRemediation
{
    public static string For(DurableJobSubstrateCause cause)
        => cause switch
        {
            DurableJobSubstrateCause.Available =>
                "No action required; the job store is in the system database.",

            DurableJobSubstrateCause.RuntimeIncomplete =>
                CapabilityUnavailableCodes.RuntimeIncompleteRemediation,

            DurableJobSubstrateCause.SystemDatabaseNotConfigured =>
                CapabilityUnavailableCodes.SystemDatabaseRemediation,

            _ => throw new ArgumentOutOfRangeException(nameof(cause), cause, null),
        };
}

public static class DurableJobSubstrateStartupGate
{
    public static void EnsureSatisfied(DurableJobSubstrateOptions options, bool requireDurableStore)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!requireDurableStore || options.SystemDatabaseConfigured)
        {
            return;
        }

        throw new DurableJobSubstrateUnavailableException(DurableJobSubstrateCause.SystemDatabaseNotConfigured);
    }
}

// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Capabilities;

// Without the system database reads keep serving, but the durable job substrate and control plane are not composed and the refusal must name that dependency.
public static class CapabilityUnavailableCodes
{
    public const string ProblemType = "https://honua.io/problems/capability-unavailable";

    public const string ErrorCode = "dependency-unavailable";

    public const string SystemDatabaseDependency = "system-database";

    // A durable job store without a runnable queue would accept a submission and never drain it.
    public const string JobQueueDependency = "job-queue";

    public const string DurableJobsCapability = "jobs.runner";

    public const string ControlPlaneProposalsCapability = "operations.proposals";

    public const string SystemDatabaseRemediation =
        "Set ConnectionStrings__DefaultConnection to the PostgreSQL system database and restart the " +
        "server. Durable jobs, the job queue, workflow definitions and governed proposals are tables in it.";

    public const string SystemDatabaseRemediationRef =
        "https://honua.io/docs/guides/deploy/troubleshooting/#database-connections";

    public const string RuntimeIncompleteRemediation =
        "A durable job store is registered without a runnable job queue, so submitted jobs could " +
        "never drain. Register the full job orchestration substrate (AddJobOrchestration) or " +
        "remove the partial registration.";

    public const string RuntimeIncompleteDetail =
        "The durable job substrate is incomplete: a job store is registered without a runnable " +
        "job queue, so a submitted job would be persisted and never drain. The request is refused " +
        "rather than accepted into a queue that does not exist.";

    public const string LongLivedHostDependency = "long-lived-host";

    public const string ServerlessManagedDetail =
        "Managed geoprocessing on this host would be queued for a background loop that stops " +
        "when the invocation ends. The request is refused instead of being accepted and never " +
        "run. Native processes run on the GDAL worker. Managed processes run on a long-lived " +
        "API host, or they finish inside the request without being queued.";

    public const string ServerlessManagedRemediation =
        "Run managed geoprocessing on a long-lived host (local Docker or ECS). On this " +
        "serverless cell, submit a native process so it runs on the GDAL worker. An operation " +
        "that completes inside the request, without admission to the local job queue, is not " +
        "refused by this check.";

    public const string ServerlessManagedRemediationRef =
        "https://honua.io/docs/guides/deploy/cloud-deployments/#background-file-imports-run-on-aws-batch";

    public const string Title = "Capability unavailable";

    public const string DurableJobStoreDetail =
        "Durable geoprocessing jobs and workflows require the job store in the system database. " +
        "This server was started without a system database, so the request is refused up front " +
        "instead of being queued and never run.";

    public const string DurableWorkflowPublicationDetail =
        "Publishing a workflow package to a Schedule target requires the durable workflow " +
        "definition store in the system database. This server was started without one, so the " +
        "compiled workflow definition could not be persisted and no scheduler would ever run it. " +
        "The publication is refused up front rather than reported as successful without a " +
        "durable record.";

    public const string DurableControlPlaneDetail =
        "The operation proposal and approval control plane requires the durable proposal store " +
        "in the system database. This server was started without one, so proposals cannot be " +
        "listed, inspected, approved, or rejected.";
}

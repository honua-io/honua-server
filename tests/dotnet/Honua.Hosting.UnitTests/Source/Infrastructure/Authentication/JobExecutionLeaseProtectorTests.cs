// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Infrastructure.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Xunit;

namespace Honua.Hosting.UnitTests.Source.Infrastructure.Authentication;

public sealed class JobExecutionLeaseProtectorTests
{
    [Fact]
    public void HistoricalProof_RequiresTerminalReceiptAndNeverEnablesExecution()
    {
        var clock = new Clock();
        var protector = new JobExecutionLeaseProtector(new EphemeralDataProtectionProvider(), clock);
        var job = Job(clock.GetUtcNow());
        var lease = protector.ProtectValidatedBinding(job, Scope(), Decision(clock), null, clock.GetUtcNow().AddHours(1));
        var accepted = Bind(job, lease);
        var terminal = accepted with { Status = ExecutionJobStatus.Succeeded };
        var active = protector.UnprotectAndMatchAuthoritativeJob(terminal);
        Assert.Throws<UnauthorizedAccessException>(() => protector.RequireVerifiedHistoricalBinding(active, terminal));
        clock.UtcNow = lease.ExpiresAt.AddDays(1);
        Assert.Throws<UnauthorizedAccessException>(() => protector.UnprotectAndMatchAuthoritativeJob(terminal));
        Assert.Throws<UnauthorizedAccessException>(() => protector.UnprotectHistoricalTerminalJob(accepted));
        var historical = protector.UnprotectHistoricalTerminalJob(terminal);
        protector.RequireVerifiedHistoricalBinding(historical, terminal);
        Assert.Throws<UnauthorizedAccessException>(() => protector.RequireVerifiedHistoricalBinding(historical,
            Bind(job, lease with { ExpiresAt = lease.ExpiresAt.AddHours(1) }) with { Status = ExecutionJobStatus.Succeeded }));
        Assert.Throws<UnauthorizedAccessException>(() => protector.RequireVerifiedCurrentBinding(historical, terminal));
        Assert.Throws<UnauthorizedAccessException>(() => protector.RequireVerifiedHistoricalBinding(historical with { }, terminal));
        Assert.Throws<UnauthorizedAccessException>(() => protector.RequireVerifiedHistoricalBinding(historical, terminal with { OperationId = "other" }));
    }

    [Fact]
    public void Protection_PinsOriginalIntentAndAllowsOnlyProgressChanges()
    {
        var clock = new Clock();
        var protector = new JobExecutionLeaseProtector(new EphemeralDataProtectionProvider(), clock);
        var job = Job(clock.GetUtcNow());
        var lease = protector.ProtectValidatedBinding(job, Scope(), Decision(clock), null, clock.GetUtcNow().AddHours(1));
        var accepted = Bind(job, lease) with { Status = ExecutionJobStatus.Running, AttemptCount = 2, Version = 9 };
        var binding = protector.UnprotectAndMatchAuthoritativeJob(accepted);
        Assert.Equal(lease.LeaseId, binding.LeaseId);
        Assert.Equal(job.OperationId, binding.JobId);
        Assert.Equal(JobExecutionLeaseIntentFingerprint.Compute(job), binding.IntentFingerprint);
    }

    [Theory]
    [InlineData("job")]
    [InlineData("parameter")]
    [InlineData("context")]
    [InlineData("version")]
    [InlineData("envelope")]
    [InlineData("expiry")]
    [InlineData("acceptance-deadline")]
    public void Protection_RefusesJobAuthorityOrEnvelopeSubstitution(string change)
    {
        var clock = new Clock();
        var protector = new JobExecutionLeaseProtector(new EphemeralDataProtectionProvider(), clock);
        var job = Job(clock.GetUtcNow());
        var lease = protector.ProtectValidatedBinding(job, Scope(), Decision(clock), null, clock.GetUtcNow().AddHours(1));
        var accepted = Bind(job, lease);
        var context = accepted.Audit.SubmitterSecurityContext!;
        var changed = change switch
        {
            "job" => accepted with { OperationId = "other" },
            "parameter" => accepted with { Spec = accepted.Spec with { Parameters = new Dictionary<string, string> { ["input"] = "other" } } },
            "context" => accepted with { Audit = accepted.Audit with { SubmitterSecurityContext = context with { Claims = [new("department", "admin")] } } },
            "version" => accepted with { Audit = accepted.Audit with { SubmitterSecurityContext = context with { AgentCredential = context.AgentCredential! with { CredentialVersion = "rotated" } } } },
            "envelope" => Bind(job, lease with { ProtectedAuthority = "invalid" }),
            "expiry" => Bind(job, lease with { ExpiresAt = lease.ExpiresAt.AddHours(1) }),
            "acceptance-deadline" => accepted with { AcceptanceDeadline = job.CreatedAt.AddMinutes(1) },
            _ => throw new InvalidOperationException()
        };
        Assert.Throws<UnauthorizedAccessException>(() => protector.UnprotectAndMatchAuthoritativeJob(changed));
    }

    [Fact]
    public void Protection_RefusesExpiryWrongPurposeAndExcessiveLifetime()
    {
        var clock = new Clock();
        var keys = new EphemeralDataProtectionProvider();
        var protector = new JobExecutionLeaseProtector(keys, clock);
        var job = Job(clock.GetUtcNow());
        var lease = protector.ProtectValidatedBinding(job, Scope(), Decision(clock), null, clock.GetUtcNow().AddHours(1));
        var accepted = Bind(job, lease);
        var otherReplicaWithoutSharedKeys = new JobExecutionLeaseProtector(new EphemeralDataProtectionProvider(), clock);
        Assert.Throws<UnauthorizedAccessException>(() => otherReplicaWithoutSharedKeys.UnprotectAndMatchAuthoritativeJob(accepted));
        Assert.Throws<UnauthorizedAccessException>(() => protector.ProtectValidatedBinding(job, Scope(),
            Decision(clock) with { MaximumExpiresAt = clock.GetUtcNow().AddDays(7) }, null, clock.GetUtcNow().AddDays(2)));
        clock.UtcNow = lease.ExpiresAt;
        Assert.Throws<UnauthorizedAccessException>(() => protector.UnprotectAndMatchAuthoritativeJob(accepted));
    }

    [Fact]
    public void Protection_RefusesMissingPolicyApprovalAndOriginalPolicyMismatch()
    {
        var clock = new Clock();
        var protector = new JobExecutionLeaseProtector(new EphemeralDataProtectionProvider(), clock);
        var job = Job(clock.GetUtcNow());
        Assert.Throws<UnauthorizedAccessException>(() => protector.ProtectValidatedBinding(job, Scope(),
            Decision(clock) with { IsAllowed = false }, null, clock.GetUtcNow().AddHours(1)));
        Assert.Throws<UnauthorizedAccessException>(() => protector.ProtectValidatedBinding(job, Scope(), Decision(clock), new()
        {
            WorkspaceId = "workspace",
            TaskId = "task",
            TaskRevision = 1,
            TaskContentHash = "content",
            AuthorityRevision = 1,
            AuthorityFingerprint = "authority",
            PolicyContextFingerprint = "substituted",
            OriginatingTurnId = "turn",
            OriginalTurnHash = "turn-hash"
        }, clock.GetUtcNow().AddHours(1)));
    }

    private static ExecutionJobRecord Bind(ExecutionJobRecord job, JobExecutionLease lease)
        => job with { Audit = job.Audit with { SubmitterSecurityContext = job.Audit.SubmitterSecurityContext! with { ExecutionLease = lease } } };

    private static JobExecutionLeaseScope Scope() => new()
    {
        Kind = ExecutionJobKind.Geoprocessing,
        Resources = [],
        ProviderBinding = "server-derived-closure",
        Operations = [new() { ResourceType = OperatorResourceType.Process, ResourceId = "analysis", Operation = OperatorOperation.Execute }]
    };

    private static JobExecutionLeasePolicyDecision Decision(Clock clock)
        => new() { IsAllowed = true, DecisionId = "canonical-benign-policy", MaximumExpiresAt = clock.GetUtcNow().AddHours(2) };

    private static ExecutionJobRecord Job(DateTimeOffset now) => new()
    {
        OperationId = "job",
        Status = ExecutionJobStatus.Queued,
        CreatedAt = now,
        UpdatedAt = now,
        Spec = new()
        {
            TargetKind = BatchComputeTargetKind.LocalProcess,
            Backend = "local",
            Kind = ExecutionJobKind.Geoprocessing,
            WorkloadName = "analysis",
            Parameters = new Dictionary<string, string> { ["input"] = "source" }
        },
        Audit = new()
        {
            RequestedBy = "actor",
            TenantId = "tenant",
            SubmitterSecurityContext = new("actor", "tenant", [new("department", "planning")])
            {
                OwnerActorId = "actor",
                AgentCredential = new()
                {
                    Kind = "api-key",
                    Id = "key",
                    CredentialVersion = "version",
                    Permissions = ["agent:workspace"],
                    TokenExpiresAt = now.AddMinutes(5)
                }
            }
        }
    };

    private sealed class Clock : TimeProvider
    {
        internal DateTimeOffset UtcNow { get; set; } = DateTimeOffset.Parse("2026-10-18T12:00:00Z", global::System.Globalization.CultureInfo.InvariantCulture);
        public override DateTimeOffset GetUtcNow() => UtcNow;
    }
}

// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.ControlPlane.Domain;
using Xunit;

namespace Honua.Core.Tests.Features.Authorization;

public sealed class JobExecutionLeaseIntentFingerprintTests
{
    [Fact]
    public void Fingerprint_CanonicalizesParameterOrderAndIgnoresExecutionProgress()
    {
        var job = Job();
        var changed = job with
        {
            Status = ExecutionJobStatus.Running,
            Version = 42,
            UpdatedAt = job.CreatedAt.AddMinutes(5),
            AttemptCount = 2,
            ClaimedBy = "worker",
            ArtifactReferences = ["new-artifact"],
            Spec = job.Spec with { Parameters = new Dictionary<string, string> { ["b"] = "2", ["a"] = "1" } }
        };
        Assert.Equal(JobExecutionLeaseIntentFingerprint.Compute(job), JobExecutionLeaseIntentFingerprint.Compute(changed));
    }

    [Theory]
    [InlineData("job")]
    [InlineData("created")]
    [InlineData("acceptance-deadline")]
    [InlineData("backend")]
    [InlineData("parameter")]
    [InlineData("policy")]
    [InlineData("credential")]
    [InlineData("expiry")]
    [InlineData("owner")]
    [InlineData("tenant")]
    [InlineData("approval")]
    [InlineData("timeout")]
    public void Fingerprint_BindsImmutableIntentAndCompleteOriginalAuthority(string field)
    {
        var job = Job();
        var context = job.Audit.SubmitterSecurityContext!;
        var changed = field switch
        {
            "job" => job with { OperationId = "other" },
            "created" => job with { CreatedAt = job.CreatedAt.AddSeconds(1) },
            "acceptance-deadline" => job with { AcceptanceDeadline = job.CreatedAt.AddMinutes(1) },
            "backend" => job with { Spec = job.Spec with { Backend = "other" } },
            "parameter" => job with { Spec = job.Spec with { Parameters = new Dictionary<string, string> { ["a"] = "different", ["b"] = "2" } } },
            "policy" => job with { Audit = job.Audit with { SubmitterSecurityContext = context with { Claims = [new("department", "admin")] } } },
            "credential" => job with { Audit = job.Audit with { SubmitterSecurityContext = context with { AgentCredential = context.AgentCredential! with { CredentialVersion = "rotated" } } } },
            "expiry" => job with { Audit = job.Audit with { SubmitterSecurityContext = context with { AgentCredential = context.AgentCredential! with { TokenExpiresAt = job.CreatedAt.AddHours(2) } } } },
            "owner" => job with { Audit = job.Audit with { RequestedBy = "other" } },
            "tenant" => job with { Audit = job.Audit with { TenantId = "other" } },
            "approval" => job with { Audit = job.Audit with { ProposalId = "other" } },
            "timeout" => job with { TimeoutPolicy = new() { MaxDuration = TimeSpan.FromHours(5) } },
            _ => throw new InvalidOperationException()
        };
        Assert.NotEqual(JobExecutionLeaseIntentFingerprint.Compute(job), JobExecutionLeaseIntentFingerprint.Compute(changed));
    }

    [Fact]
    public void Fingerprint_ExcludesOnlyTheSeparateExecutionLeaseFromOriginalContext()
    {
        var job = Job();
        var leased = job with
        {
            Audit = job.Audit with
            {
                SubmitterSecurityContext = job.Audit.SubmitterSecurityContext! with
                {
                    ExecutionLease = new() { LeaseId = "lease", JobId = job.OperationId, IssuedAt = job.CreatedAt,
                        ExpiresAt = job.CreatedAt.AddHours(1), ProtectedAuthority = "protected" }
                }
            }
        };
        Assert.Equal(JobExecutionLeaseIntentFingerprint.Compute(job), JobExecutionLeaseIntentFingerprint.Compute(leased));
    }

    [Fact]
    public void Fingerprint_RefusesOversizedIntentWithoutTruncation()
    {
        var job = Job();
        Assert.Throws<UnauthorizedAccessException>(() => JobExecutionLeaseIntentFingerprint.Compute(job with
        {
            Spec = job.Spec with { Parameters = new Dictionary<string, string> { ["input"] = new('x', 65537) } }
        }));
        Assert.Throws<UnauthorizedAccessException>(() => JobExecutionLeaseIntentFingerprint.Compute(job with
        {
            Spec = job.Spec with { Parameters = Enumerable.Range(0, 32).ToDictionary(i => i.ToString(System.Globalization.CultureInfo.InvariantCulture), _ => new string('x', 65536)) }
        }));
    }

    private static ExecutionJobRecord Job()
    {
        var now = DateTimeOffset.Parse("2026-10-18T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        return new()
        {
            OperationId = "job", Status = ExecutionJobStatus.Queued, CreatedAt = now, UpdatedAt = now,
            Spec = new() { TargetKind = BatchComputeTargetKind.LocalProcess, Backend = "local", Kind = ExecutionJobKind.Geoprocessing,
                WorkloadName = "analysis", Parameters = new Dictionary<string, string> { ["a"] = "1", ["b"] = "2" } },
            Audit = new()
            {
                RequestedBy = "actor", TenantId = "tenant", ProposalId = "proposal",
                SubmitterSecurityContext = new("actor", "tenant", [new("department", "planning")])
                {
                    OwnerActorId = "actor", AgentCredential = new() { Kind = "api-key", Id = "key", CredentialVersion = "version",
                        Permissions = ["agent:workspace"], TokenExpiresAt = now.AddHours(1) }
                }
            }
        };
    }
}

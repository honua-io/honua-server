// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.RegularExpressions;
using Honua.Geoprocessing.Release;

namespace Honua.Geoprocessing.ProcessRelease;

/// <summary>
/// The pure admissibility rules for activating a release (plan v2 section 4, mirrored by
/// honua-release's <c>gp_process_release.py preflight</c>): the lock must exist and belong to
/// the process, the caller's binding version must be the live one, the server contract must be
/// inside the lock's window, every declared target must be executable here and carry its
/// provider identity, and a rollback must name exactly the previous release. Stores call this
/// under their concurrency guard; it never touches storage itself.
/// </summary>
public static partial class GpProcessReleaseActivationPolicy
{
    /// <summary>The target kind name for Kubernetes Jobs.</summary>
    public const string KubernetesJobTarget = "kubernetes-job";

    /// <summary>The target kind name for AWS Batch.</summary>
    public const string AwsBatchTarget = "aws-batch";

    /// <summary>Evaluates an activation and, when admissible, returns the binding to write.</summary>
    /// <param name="activation">The request.</param>
    /// <param name="release">The published lock for the requested digest, or null.</param>
    /// <param name="current">The live binding, or null when the process was never bound.</param>
    /// <param name="environment">What this server can execute.</param>
    /// <param name="now">The activation time.</param>
    /// <returns>The decision; <see cref="GpProcessBindingDecision.Binding"/> is the new binding when activated.</returns>
    public static GpProcessBindingDecision Evaluate(
        GpProcessBindingActivation activation,
        GpProcessReleaseRecord? release,
        GpProcessBinding? current,
        GpProcessReleaseEnvironment environment,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(activation);
        ArgumentNullException.ThrowIfNull(environment);
        var reasons = new List<string>();
        var details = new List<string>();
        void Refuse(string code, string detail)
        {
            reasons.Add(code);
            details.Add(detail);
        }

        if (activation.Operation is not ("deploy" or "rollback"))
        {
            Refuse(GpProcessBindingRefusal.OperationInvalid, $"operation must be deploy or rollback, not '{activation.Operation}'");
        }

        var liveVersion = current?.BindingVersion ?? 0;
        if (activation.ExpectedBindingVersion < 0 || liveVersion == int.MaxValue
            || activation.ExpectedBindingVersion != liveVersion)
        {
            Refuse(GpProcessBindingRefusal.StaleBindingVersion,
                $"the live binding version for '{activation.Process}' is {liveVersion}, the activation expected {activation.ExpectedBindingVersion}; re-read the binding and retry from the current state");
        }

        if (release is null)
        {
            Refuse(GpProcessBindingRefusal.LockNotFound, $"no published release has digest {activation.LockDigest}; publish the lock first");
            return Refused(reasons, details, current);
        }

        var lockDocument = release.Lock;
        if (!string.Equals(lockDocument.Process.Name, activation.Process, StringComparison.Ordinal))
        {
            Refuse(GpProcessBindingRefusal.ProcessMismatch, $"lock {release.Digest} is for process '{lockDocument.Process.Name}', not '{activation.Process}'");
        }

        var window = lockDocument.Compatibility.ServerContractVersion;
        if (environment.ServerContractVersion < window.Minimum || environment.ServerContractVersion > window.Maximum)
        {
            Refuse(GpProcessBindingRefusal.IncompatibleServerContract,
                $"this server runs execution contract {environment.ServerContractVersion}; the release supports {window.Minimum}..{window.Maximum}");
        }

        var targets = new List<GpProcessBindingTarget>();
        foreach (var target in lockDocument.Targets)
        {
            if (!environment.SupportedTargetKinds.Contains(target.Kind, StringComparer.Ordinal))
            {
                Refuse(GpProcessBindingRefusal.TargetUnsupported, $"no activatable execution workload of kind '{target.Kind}' is registered on this server");
                continue;
            }

            if (target.Kind == KubernetesJobTarget && !environment.CustomCodeMayUseKubernetes)
            {
                Refuse(GpProcessBindingRefusal.TargetUnsupported,
                    "custom-code processes execute on AWS Batch only (ADR-0063); a kubernetes-job target cannot be activated for this release");
                continue;
            }

            var identities = activation.Targets.Where(t => string.Equals(t.Kind, target.Kind, StringComparison.Ordinal)).ToArray();
            if (identities.Length > 1)
            {
                Refuse(GpProcessBindingRefusal.TargetIdentityMissing, "each target must have exactly one provider identity");
                continue;
            }

            var identity = identities.FirstOrDefault();
            string? jobDefinitionArn = null;
            if (target.Kind == AwsBatchTarget)
            {
                jobDefinitionArn = identity?.JobDefinitionArn?.Trim();
                if (string.IsNullOrEmpty(jobDefinitionArn) || !RevisionArn().IsMatch(jobDefinitionArn)
                    || (target.JobDefinitionName is not null
                        && !jobDefinitionArn.Contains(":job-definition/" + target.JobDefinitionName + ":", StringComparison.Ordinal)))
                {
                    Refuse(GpProcessBindingRefusal.TargetIdentityMissing,
                        "an aws-batch target needs the registered job definition ARN with its revision (arn:…:job-definition/<name>:<revision>) so admitted jobs pin that revision");
                    continue;
                }
            }

            var image = ImageForTarget(lockDocument, target.WorkerImageDigest);
            if (!environment.ReadyTargets.Any(ready =>
                string.Equals(ready.Kind, target.Kind, StringComparison.Ordinal)
                && string.Equals(ready.Image, image, StringComparison.Ordinal)
                && string.Equals(ready.JobDefinitionArn, jobDefinitionArn, StringComparison.Ordinal)))
            {
                Refuse(GpProcessBindingRefusal.TargetNotReady,
                    "the pinned worker image and provider revision must be registered on a compatible execution workload before activation");
                continue;
            }

            targets.Add(new GpProcessBindingTarget
            {
                Kind = target.Kind,
                Image = image,
                JobDefinitionArn = jobDefinitionArn,
            });
        }

        if (activation.Operation == "rollback")
        {
            if (current?.PreviousLockDigest is null)
            {
                Refuse(GpProcessBindingRefusal.RollbackTargetMissing, $"'{activation.Process}' has no previous release to roll back to");
            }
            else if (!string.Equals(current.PreviousLockDigest, release.Digest, StringComparison.Ordinal))
            {
                Refuse(GpProcessBindingRefusal.RollbackTargetMismatch,
                    $"rollback must target the previous release {current.PreviousLockDigest}, not {release.Digest}");
            }
        }

        if (reasons.Count > 0)
        {
            return Refused(reasons, details, current);
        }

        return new GpProcessBindingDecision
        {
            Activated = true,
            Reasons = [],
            Details = [],
            Binding = new GpProcessBinding
            {
                Process = activation.Process,
                BindingVersion = liveVersion + 1,
                LockDigest = release.Digest,
                PreviousLockDigest = current?.LockDigest,
                Targets = targets.AsReadOnly(),
                Operation = activation.Operation,
                ActivatedBy = activation.Actor,
                ActivatedAt = now,
            },
        };
    }

    /// <summary>
    /// The image a target runs: the lock's runtime image repository pinned at the target's worker
    /// digest. When the target digest equals the runtime image digest this is the runtime image
    /// reference itself.
    /// </summary>
    public static string ImageForTarget(GpProcessReleaseLock lockDocument, string workerImageDigest)
    {
        ArgumentNullException.ThrowIfNull(lockDocument);
        var reference = lockDocument.RuntimeImage.Reference;
        var at = reference.IndexOf('@', StringComparison.Ordinal);
        return at < 0 ? reference + "@" + workerImageDigest : reference[..at] + "@" + workerImageDigest;
    }

    [GeneratedRegex(@"^arn:aws(?:-[a-z0-9-]+)?:batch:[a-z0-9-]+:[0-9]{12}:job-definition/[A-Za-z0-9_-]+:[1-9][0-9]*$", RegexOptions.CultureInvariant)]
    private static partial Regex RevisionArn();

    private static GpProcessBindingDecision Refused(List<string> reasons, List<string> details, GpProcessBinding? current)
        => new()
        {
            Activated = false,
            Reasons = reasons,
            Details = details,
            Binding = current,
        };
}

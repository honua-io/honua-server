// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Compliance.Abstractions;
using Honua.Core.Features.Compliance.Domain;
using Microsoft.Extensions.Options;

namespace Honua.Core.Features.Compliance.Services;

/// <summary>
/// Walks the control catalog, queries the dependency gate, residency policy, and
/// encryption posture, and rolls everything up into a <see cref="ComplianceSnapshot"/>.
/// </summary>
/// <remarks>
/// <para>
/// Each control gets one evidence row per declared dependency plus a synthetic
/// "claim" row describing the operator-attested readiness for the framework. The
/// control's rolled-up <see cref="ComplianceControlStatus"/> is the worst case
/// across all its evidence rows — "the chain is as strong as its weakest link"
/// keeps the dashboard honest.
/// </para>
/// </remarks>
internal sealed class DefaultComplianceEvidenceCollector : IComplianceEvidenceCollector
{
    private static readonly string ServerVersion = ResolveServerVersion();

    private readonly IComplianceControlCatalog _catalog;
    private readonly IComplianceDependencyGate _gate;
    private readonly IDataResidencyPolicyProvider _residency;
    private readonly IEncryptionPostureProvider _encryption;
    private readonly IOptionsMonitor<ComplianceOptions> _options;
    private readonly TimeProvider _timeProvider;

    public DefaultComplianceEvidenceCollector(
        IComplianceControlCatalog catalog,
        IComplianceDependencyGate gate,
        IDataResidencyPolicyProvider residency,
        IEncryptionPostureProvider encryption,
        IOptionsMonitor<ComplianceOptions> options,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(residency);
        ArgumentNullException.ThrowIfNull(encryption);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _catalog = catalog;
        _gate = gate;
        _residency = residency;
        _encryption = encryption;
        _options = options;
        _timeProvider = timeProvider;
    }

    public Task<ComplianceSnapshot> CollectAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var now = _timeProvider.GetUtcNow();
        var opts = _options.CurrentValue;
        var encryption = _encryption.GetPosture();
        var residency = _residency.GetPolicy();

        var rows = new List<ComplianceControlEvidenceRow>(_catalog.Controls.Count);
        int implemented = 0, partial = 0, notImpl = 0, na = 0, unknown = 0;

        foreach (var control in _catalog.Controls)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var row = BuildRow(control, now, opts, encryption, residency);
            rows.Add(row);

            switch (row.Status)
            {
                case ComplianceControlStatus.Implemented: implemented++; break;
                case ComplianceControlStatus.PartiallyImplemented: partial++; break;
                case ComplianceControlStatus.NotImplemented: notImpl++; break;
                case ComplianceControlStatus.NotApplicable: na++; break;
                default: unknown++; break;
            }
        }

        var applicableTotal = implemented + partial + notImpl;
        var readinessPercent = applicableTotal == 0
            ? 0d
            : Math.Round(implemented * 100d / applicableTotal, 2, MidpointRounding.AwayFromZero);

        var summary = new ComplianceReadinessSummary
        {
            Implemented = implemented,
            PartiallyImplemented = partial,
            NotImplemented = notImpl,
            NotApplicable = na,
            Unknown = unknown,
            ReadinessPercent = readinessPercent,
        };

        return Task.FromResult(new ComplianceSnapshot
        {
            GeneratedAt = now,
            ServerVersion = ServerVersion,
            Controls = rows.AsReadOnly(),
            Encryption = encryption,
            Residency = residency,
            Summary = summary,
        });
    }

    private ComplianceControlEvidenceRow BuildRow(
        ComplianceControl control,
        DateTimeOffset now,
        ComplianceOptions opts,
        EncryptionPosture encryption,
        DataResidencyPolicy residency)
    {
        var evidence = new List<ComplianceEvidence>(control.Dependencies.Count + 2);
        var gaps = new List<string>();
        var frameworkClaimed = control.Framework switch
        {
            ComplianceFramework.Soc2 => opts.Soc2ReadinessClaimed,
            ComplianceFramework.FedRamp => opts.FedRampReadinessClaimed,
            _ => false,
        };

        if (!frameworkClaimed)
        {
            evidence.Add(new ComplianceEvidence
            {
                ControlId = control.ControlId,
                CollectedAt = now,
                Source = "configuration",
                Claim = $"Operator has not claimed {control.Framework} readiness for this deployment.",
                Status = ComplianceControlStatus.NotApplicable,
                Detail = $"Compliance:{(control.Framework == ComplianceFramework.Soc2 ? "Soc2" : "FedRamp")}ReadinessClaimed=false",
            });

            return new ComplianceControlEvidenceRow
            {
                Control = control,
                Status = ComplianceControlStatus.NotApplicable,
                Evidence = evidence.AsReadOnly(),
                Gaps = Array.Empty<string>(),
            };
        }

        var status = ComplianceControlStatus.Implemented;
        var unsatisfiedDependencies = 0;

        foreach (var dependency in control.Dependencies)
        {
            var satisfied = _gate.IsSatisfied(dependency);
            var description = _gate.DescribeStatus(dependency);
            evidence.Add(new ComplianceEvidence
            {
                ControlId = control.ControlId,
                CollectedAt = now,
                Source = "dependency-gate",
                Claim = $"Dependency {dependency} is {(satisfied ? "satisfied" : "not satisfied")}.",
                Status = satisfied ? ComplianceControlStatus.Implemented : ComplianceControlStatus.NotImplemented,
                Detail = description,
            });

            if (!satisfied)
            {
                status = ComplianceControlStatus.PartiallyImplemented;
                unsatisfiedDependencies++;
                gaps.Add($"{dependency}: {description}");
            }
        }

        AppendFrameworkSpecificEvidence(control, evidence, gaps, ref status, encryption, residency, now);

        // Downgrade to NotImplemented when every dependency is unsatisfied. Track this
        // off `unsatisfiedDependencies` rather than `gaps.Count` because framework-
        // specific evidence (FIPS, residency) can also append to `gaps`, which would
        // otherwise hide the all-failed case behind a PartiallyImplemented rollup.
        if (control.Dependencies.Count > 0 && unsatisfiedDependencies == control.Dependencies.Count)
        {
            status = ComplianceControlStatus.NotImplemented;
        }

        return new ComplianceControlEvidenceRow
        {
            Control = control,
            Status = status,
            Evidence = evidence.AsReadOnly(),
            Gaps = gaps.AsReadOnly(),
        };
    }

    private static void AppendFrameworkSpecificEvidence(
        ComplianceControl control,
        List<ComplianceEvidence> evidence,
        List<string> gaps,
        ref ComplianceControlStatus status,
        EncryptionPosture encryption,
        DataResidencyPolicy residency,
        DateTimeOffset now)
    {
        if (control.Framework == ComplianceFramework.FedRamp
            && (control.ControlId.Contains("sc-13", StringComparison.OrdinalIgnoreCase)
                || control.ControlId.Contains("sc-28", StringComparison.OrdinalIgnoreCase)
                || control.ControlId.Contains("sc-8", StringComparison.OrdinalIgnoreCase)))
        {
            evidence.Add(new ComplianceEvidence
            {
                ControlId = control.ControlId,
                CollectedAt = now,
                Source = "encryption-posture",
                Claim = $"FIPS mode reported as {(encryption.FipsMode ? "enabled" : "disabled")} via {encryption.FipsSource}.",
                Status = encryption.FipsMode ? ComplianceControlStatus.Implemented : ComplianceControlStatus.PartiallyImplemented,
                Detail = $"algorithms=[{string.Join(", ", encryption.Algorithms)}], activeKeyVersion={encryption.ActiveKeyVersion}",
            });

            if (!encryption.FipsMode)
            {
                status = ComplianceControlStatus.PartiallyImplemented;
                gaps.Add("FIPS 140-2 mode is not attested — set Compliance:Encryption:FipsModeAttested or run on a FIPS-enabled host.");
            }
        }

        if ((control.ControlId.Equals("fedramp.sc-7", StringComparison.OrdinalIgnoreCase)
                || control.ControlId.Equals("soc2.cc6.7", StringComparison.OrdinalIgnoreCase))
            && !residency.Enforced)
        {
            evidence.Add(new ComplianceEvidence
            {
                ControlId = control.ControlId,
                CollectedAt = now,
                Source = "residency-policy",
                Claim = "Data residency policy is not enforced.",
                Status = ComplianceControlStatus.PartiallyImplemented,
                Detail = $"primaryRegion={residency.PrimaryRegion}, allowedRegions=[{string.Join(", ", residency.AllowedRegions)}]",
            });

            // Mirror the FIPS gap path: a framework-specific evidence row that downgrades
            // status must also surface a gap message so the dashboard's Gaps section and
            // the PDF report render the issue — otherwise the row drops to
            // PartiallyImplemented with an empty Gaps[] and the operator sees no signal.
            gaps.Add("Data residency policy is informational only — set Compliance:DataResidency:Enforced=true to deny egress to regions outside the allow-list.");

            if (status == ComplianceControlStatus.Implemented)
            {
                status = ComplianceControlStatus.PartiallyImplemented;
            }
        }
    }

    private static string ResolveServerVersion()
    {
        // AssemblyName.Version is trim-/AOT-safe; GetCustomAttribute<T> is not.
        var version = typeof(DefaultComplianceEvidenceCollector).Assembly.GetName().Version;
        return version is null
            ? "0.0.0"
            : version.ToString(fieldCount: 4);
    }
}

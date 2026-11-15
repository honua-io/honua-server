// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Observability.Domain;
using Honua.Infrastructure.RateLimiting;

namespace Honua.Infrastructure.Monitoring;

internal sealed partial class OpsFindingsService
{
    internal const string RuleAnonymousPressureAdmission = "anonymous-pressure-admission";

    private void EvaluateAnonymousAdmissionPressure(DateTimeOffset now, List<OpsFinding> findings)
    {
        if (_admissionGate is null ||
            !AnonymousPressureAdmissionMiddleware.IsUnderPressure(_admissionGate.GetPressure()))
        {
            return;
        }

        var subject = new OpsFindingSubject();
        findings.Add(new OpsFinding
        {
            Id = OpsFindingId.Create(RuleAnonymousPressureAdmission, subject),
            Rule = RuleAnonymousPressureAdmission,
            Severity = OpsFindingSeverity.Warning,
            Title = "Expensive anonymous requests are temporarily limited",
            Explanation = "Database admission pressure is elevated. Expensive anonymous requests receive a retry response to preserve headroom for signed-in users. Normal admission returns automatically when current pressure clears. This signal alone does not establish an attack.",
            DetectedAt = now,
            Subject = subject,
            EvidenceRefs = ["GET /api/v1/admin/observability/ops-health"],
        });
    }

    private EvidenceSourceEnvelope BuildAdmissionPressureSource(DateTimeOffset now)
        => _admissionGate is null
            ? EvidencePostureFactory.NotConfigured(
                EvidencePostureVocabulary.SourceIds.FindingsAdmissionPressure,
                EvidencePostureVocabulary.BackendKinds.InProcess,
                "bounded-database-admission")
            : EvidencePostureFactory.Complete(
                EvidencePostureVocabulary.SourceIds.FindingsAdmissionPressure,
                EvidencePostureVocabulary.BackendKinds.InProcess,
                "bounded-database-admission", now, SignalValidity);
}

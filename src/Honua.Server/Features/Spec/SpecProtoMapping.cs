// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Spec.Abstractions;
using Honua.Core.Features.Spec.Domain;
using Proto = Geospatial.V1;

namespace Honua.Server.Features.Spec;

/// <summary>
/// Translates between the spec domain records and the generated
/// <c>geospatial.v1</c> proto messages.
/// </summary>
internal static class SpecProtoMapping
{
    public static CanonicalSpecDocument FromProto(Proto.CanonicalSpecDocument proto)
    {
        ArgumentNullException.ThrowIfNull(proto);

        // Reject unspecified kinds AND undefined numeric enum values at the
        // transport boundary instead of silently coercing them to Compute (see
        // finding: unknown-kind). Proto enums are wire-compatible with any int,
        // so a forward-incompatible client that sends (SpecResourceKind)999
        // would otherwise slip past the Unspecified guard and dispatch as
        // Compute. The REST layer mirrors this via SpecNodeRequest.Kind being
        // nullable.
        List<SpecWarning>? fatal = null;
        foreach (var n in proto.Nodes)
        {
            if (!IsDefinedResourceKind(n.Kind))
            {
                fatal ??= new List<SpecWarning>();
                fatal.Add(new SpecWarning
                {
                    Code = SpecDiagnosticCodes.UnknownKind,
                    Message = $"Node '{n.Id}' does not declare a resource kind.",
                    Severity = SpecDiagnosticSeverity.Error,
                    NodeId = n.Id,
                    Remedy = "Set kind to one of: COMPUTE, REPORT, DATASET, SERVICE, APP."
                });
            }
        }

        if (fatal is not null)
        {
            throw new SpecDocumentInvalidException(fatal);
        }

        var nodes = new List<CanonicalSpecNode>(proto.Nodes.Count);
        foreach (var n in proto.Nodes)
        {
            nodes.Add(new CanonicalSpecNode
            {
                Id = n.Id,
                Kind = FromProto(n.Kind),
                Op = n.HasOp ? n.Op : null,
                Inputs = new Dictionary<string, string>(n.Inputs, StringComparer.Ordinal),
                Parameters = new Dictionary<string, string>(n.Parameters, StringComparer.Ordinal),
                CanonicalFragment = n.HasCanonicalFragment ? n.CanonicalFragment : null,
                SourcePins = new Dictionary<string, string>(n.SourcePins, StringComparer.Ordinal),
                Nondeterministic = n.Nondeterministic
            });
        }

        return new CanonicalSpecDocument
        {
            GrammarVersion = proto.GrammarVersion,
            ProcessFamilyVersion = proto.ProcessFamilyVersion,
            SpecId = proto.HasSpecId ? proto.SpecId : null,
            Nodes = nodes
        };
    }

    public static Proto.SpecPlan ToProto(SpecPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var proto = new Proto.SpecPlan
        {
            PlanId = plan.PlanId,
            GrammarVersion = plan.GrammarVersion,
            ProcessFamilyVersion = plan.ProcessFamilyVersion
        };

        foreach (var n in plan.Nodes)
        {
            var node = new Proto.SpecPlanNode
            {
                NodeId = n.NodeId,
                Kind = ToProto(n.Kind),
                ContentHash = n.ContentHash,
                Cost = ToProto(n.Cost)
            };
            if (n.Op is not null)
            {
                node.Op = n.Op;
            }

            node.DependsOn.AddRange(n.DependsOn);
            foreach (var w in n.Warnings)
            {
                node.Warnings.Add(ToProto(w));
            }

            proto.Nodes.Add(node);
        }

        foreach (var w in plan.Warnings)
        {
            proto.Warnings.Add(ToProto(w));
        }

        return proto;
    }

    public static Proto.ApplySpecEvent ToProto(SpecApplyEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);

        var proto = new Proto.ApplySpecEvent
        {
            Sequence = evt.Sequence,
            Kind = ToProto(evt.Kind),
            ApplyToken = evt.ApplyToken,
            TimestampUnixMs = evt.Timestamp.ToUnixTimeMilliseconds()
        };

        if (evt.NodeId is not null)
        {
            proto.NodeId = evt.NodeId;
        }

        if (evt.ContentHash is not null)
        {
            proto.ContentHash = evt.ContentHash;
        }

        if (evt.Diagnostic is not null)
        {
            proto.Diagnostic = ToProto(evt.Diagnostic);
        }

        if (evt.ActualCost is not null)
        {
            proto.ActualCost = ToProto(evt.ActualCost);
        }

        if (evt.Summary is not null)
        {
            proto.Summary = ToProto(evt.Summary);
        }

        return proto;
    }

    public static SpecCacheMode FromProto(Proto.SpecCacheMode mode) => mode switch
    {
        // Unspecified is the wire default and mirrors "omitted" on the REST
        // side; both paths coerce to ReadWrite per the published contract.
        Proto.SpecCacheMode.Unspecified => SpecCacheMode.ReadWrite,
        Proto.SpecCacheMode.ReadWrite => SpecCacheMode.ReadWrite,
        Proto.SpecCacheMode.ReadOnly => SpecCacheMode.ReadOnly,
        Proto.SpecCacheMode.Bypass => SpecCacheMode.Bypass,
        // Invariant: HonuaSpecService.ApplySpec rejects undefined numeric
        // values with unknown-cache-mode before this mapping runs, so reaching
        // the default arm means the caller bypassed the boundary check. Throw
        // rather than silently coerce to ReadWrite.
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "SpecCacheMode must be validated before mapping.")
    };

    public static bool IsDefinedCacheMode(Proto.SpecCacheMode mode) => mode switch
    {
        Proto.SpecCacheMode.Unspecified => true,
        Proto.SpecCacheMode.ReadWrite => true,
        Proto.SpecCacheMode.ReadOnly => true,
        Proto.SpecCacheMode.Bypass => true,
        _ => false
    };

    public static Proto.SpecResourceKind ToProto(SpecResourceKind kind) => kind switch
    {
        SpecResourceKind.Compute => Proto.SpecResourceKind.Compute,
        SpecResourceKind.Report => Proto.SpecResourceKind.Report,
        SpecResourceKind.Dataset => Proto.SpecResourceKind.Dataset,
        SpecResourceKind.Service => Proto.SpecResourceKind.Service,
        SpecResourceKind.App => Proto.SpecResourceKind.App,
        _ => Proto.SpecResourceKind.Unspecified
    };

    public static SpecResourceKind FromProto(Proto.SpecResourceKind kind) => kind switch
    {
        Proto.SpecResourceKind.Compute => SpecResourceKind.Compute,
        Proto.SpecResourceKind.Report => SpecResourceKind.Report,
        Proto.SpecResourceKind.Dataset => SpecResourceKind.Dataset,
        Proto.SpecResourceKind.Service => SpecResourceKind.Service,
        Proto.SpecResourceKind.App => SpecResourceKind.App,
        // Invariant: FromProto(CanonicalSpecDocument) rejects Unspecified and
        // undefined numeric values with unknown-kind before this mapping runs,
        // so reaching the default arm means the caller bypassed the boundary
        // check. Throw rather than silently coerce to Compute.
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "SpecResourceKind must be validated before mapping.")
    };

    private static bool IsDefinedResourceKind(Proto.SpecResourceKind kind) => kind switch
    {
        Proto.SpecResourceKind.Compute => true,
        Proto.SpecResourceKind.Report => true,
        Proto.SpecResourceKind.Dataset => true,
        Proto.SpecResourceKind.Service => true,
        Proto.SpecResourceKind.App => true,
        _ => false
    };

    public static Proto.SpecApplyEventKind ToProto(SpecApplyEventKind kind) => kind switch
    {
        SpecApplyEventKind.Queued => Proto.SpecApplyEventKind.Queued,
        SpecApplyEventKind.Running => Proto.SpecApplyEventKind.Running,
        SpecApplyEventKind.Cached => Proto.SpecApplyEventKind.Cached,
        SpecApplyEventKind.Succeeded => Proto.SpecApplyEventKind.Succeeded,
        SpecApplyEventKind.Failed => Proto.SpecApplyEventKind.Failed,
        SpecApplyEventKind.Skipped => Proto.SpecApplyEventKind.Skipped,
        SpecApplyEventKind.Warning => Proto.SpecApplyEventKind.Warning,
        SpecApplyEventKind.ApplyStarted => Proto.SpecApplyEventKind.ApplyStarted,
        SpecApplyEventKind.ApplyCompleted => Proto.SpecApplyEventKind.ApplyCompleted,
        SpecApplyEventKind.ApplyCancelled => Proto.SpecApplyEventKind.ApplyCancelled,
        _ => Proto.SpecApplyEventKind.Unspecified
    };

    public static Proto.SpecDiagnostic ToProto(SpecWarning warning)
    {
        var proto = new Proto.SpecDiagnostic
        {
            Code = warning.Code,
            Message = warning.Message,
            Severity = ToProto(warning.Severity)
        };

        if (warning.NodeId is not null)
        {
            proto.NodeId = warning.NodeId;
        }

        if (warning.Remedy is not null)
        {
            proto.Remedy = warning.Remedy;
        }

        return proto;
    }

    public static Proto.SpecDiagnosticSeverity ToProto(SpecDiagnosticSeverity severity) => severity switch
    {
        SpecDiagnosticSeverity.Info => Proto.SpecDiagnosticSeverity.Info,
        SpecDiagnosticSeverity.Warning => Proto.SpecDiagnosticSeverity.Warning,
        SpecDiagnosticSeverity.Error => Proto.SpecDiagnosticSeverity.Error,
        _ => Proto.SpecDiagnosticSeverity.Unspecified
    };

    public static Proto.SpecCostEstimate ToProto(SpecCostEstimate cost)
    {
        var proto = new Proto.SpecCostEstimate();
        if (cost.EstimatedRows is long r)
        {
            proto.EstimatedRows = r;
        }

        if (cost.EstimatedBytes is long b)
        {
            proto.EstimatedBytes = b;
        }

        if (cost.EstimatedDurationMs is double d)
        {
            proto.EstimatedDurationMs = d;
        }

        return proto;
    }

    public static Proto.SpecCostActual ToProto(SpecCostActual cost)
    {
        var proto = new Proto.SpecCostActual
        {
            DurationMs = cost.DurationMs
        };
        if (cost.Rows is long r)
        {
            proto.Rows = r;
        }

        if (cost.Bytes is long b)
        {
            proto.Bytes = b;
        }

        return proto;
    }

    public static Proto.SpecApplySummary ToProto(SpecApplySummary summary) => new()
    {
        TotalNodes = summary.TotalNodes,
        CachedNodes = summary.CachedNodes,
        RanNodes = summary.RanNodes,
        FailedNodes = summary.FailedNodes,
        SkippedNodes = summary.SkippedNodes,
        TotalDurationMs = summary.TotalDurationMs,
        Cancelled = summary.Cancelled
    };
}

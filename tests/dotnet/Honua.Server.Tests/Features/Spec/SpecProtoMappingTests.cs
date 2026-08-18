// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Spec.Abstractions;
using Honua.Core.Features.Spec.Domain;
using Honua.Server.Features.Spec;
using Honua.TestKit.Attributes;
using Proto = Geospatial.V1;

namespace Honua.Server.Tests.Features.Spec;

/// <summary>
/// Unit tests pinning the SpecService side of the Geospatial.Grpc 0.2.0-alpha.1 convergence:
/// <c>SpecDiagnostic</c> folded into <c>ErrorDetail</c>, <c>SpecCostEstimate</c> /
/// <c>SpecCostActual</c> folded into <c>DryRunResult</c>, <c>apply_token</c> replaced by
/// <c>job_id</c>, and <c>CanonicalSpecNode</c> inputs/parameters retyped to
/// <c>ParameterValue</c>.
/// </summary>
public sealed class SpecProtoMappingTests
{
    [UnitTest]
    public void ToProto_SpecWarning_CarriesSymbolicCodeInDetails()
    {
        // ErrorDetail.code is an int32 and the spec diagnostic code is a kebab-case symbol,
        // so the symbol travels in details["error_code"]. Admin tooling keys off it.
        var warning = new SpecWarning
        {
            Code = SpecDiagnosticCodes.DagCycle,
            Message = "Cycle detected.",
            Severity = SpecDiagnosticSeverity.Error,
            NodeId = "node-a",
            Remedy = "Break the cycle."
        };

        var proto = SpecProtoMapping.ToProto(warning);

        proto.Details.Should().ContainKey("error_code")
            .WhoseValue.Should().Be(SpecDiagnosticCodes.DagCycle);
        proto.Message.Should().Be("Cycle detected.");
        proto.Severity.Should().Be(Proto.Severity.Error);
        proto.Category.Should().Be(Proto.ErrorCategory.Validation);
        proto.NodeId.Should().Be("node-a");
        proto.Remedy.Should().Be("Break the cycle.");
    }

    [UnitTest]
    public void ToProto_SpecDiagnosticSeverity_UsesAscendingSharedSeverity()
    {
        // The retired IssueSeverity/SpecDiagnosticSeverity enums numbered ERROR=1; the shared
        // Severity is ordered ascending by seriousness (INFO=1 < WARNING=2 < ERROR=3).
        SpecProtoMapping.ToProto(SpecDiagnosticSeverity.Info).Should().Be(Proto.Severity.Info);
        SpecProtoMapping.ToProto(SpecDiagnosticSeverity.Warning).Should().Be(Proto.Severity.Warning);
        SpecProtoMapping.ToProto(SpecDiagnosticSeverity.Error).Should().Be(Proto.Severity.Error);

        ((int)Proto.Severity.Info).Should().BeLessThan((int)Proto.Severity.Warning);
        ((int)Proto.Severity.Warning).Should().BeLessThan((int)Proto.Severity.Error);
    }

    [UnitTest]
    public void ToProto_SpecCostEstimate_PopulatesDryRunResultEstimateFields()
    {
        var proto = SpecProtoMapping.ToProto(new SpecCostEstimate
        {
            EstimatedRows = 12,
            EstimatedBytes = 3_400,
            EstimatedDurationMs = 56.5
        });

        proto.EstimatedRows.Should().Be(12);
        proto.EstimatedBytes.Should().Be(3_400);
        proto.EstimatedDurationMs.Should().Be(56.5);

        // The estimate lands only on fields 5-7; the operator-service envelope stays untouched.
        proto.EstimatedDurationSeconds.Should().Be(0);
        proto.ActualRows.Should().Be(0);
        proto.ActualBytes.Should().Be(0);
        proto.ActualDurationMs.Should().Be(0);
    }

    [UnitTest]
    public void ToProto_SpecCostActual_PopulatesDryRunResultActualFields()
    {
        var proto = SpecProtoMapping.ToProto(new SpecCostActual
        {
            Rows = 7,
            Bytes = 89,
            DurationMs = 1.5
        });

        proto.ActualRows.Should().Be(7);
        proto.ActualBytes.Should().Be(89);
        proto.ActualDurationMs.Should().Be(1.5);

        proto.EstimatedRows.Should().Be(0);
        proto.EstimatedBytes.Should().Be(0);
        proto.EstimatedDurationMs.Should().Be(0);
    }

    [UnitTest]
    public void ToProto_ApplyEvent_EmitsApplyTokenAsJobId()
    {
        var proto = SpecProtoMapping.ToProto(new SpecApplyEvent
        {
            Sequence = 3,
            Kind = SpecApplyEventKind.Running,
            ApplyToken = "run-42",
            Timestamp = DateTimeOffset.UnixEpoch
        });

        proto.JobId.Should().Be("run-42");
    }

    [UnitTest]
    public void FromProto_StringParameterValues_ReadIntoTheDomainDictionaries()
    {
        var document = BuildDocument(node =>
        {
            node.Inputs["src"] = new Proto.ParameterValue { StringValue = "@upstream" };
            node.Parameters["mode"] = new Proto.ParameterValue { StringValue = "fast" };
        });

        var domain = SpecProtoMapping.FromProto(document);

        domain.Nodes.Should().ContainSingle();
        domain.Nodes[0].Inputs.Should().ContainKey("src").WhoseValue.Should().Be("@upstream");
        domain.Nodes[0].Parameters.Should().ContainKey("mode").WhoseValue.Should().Be("fast");
    }

    [UnitTest]
    public void FromProto_UnsetParameterValue_ReadsAsEmptyString()
    {
        // map<string, string> could carry "", so an unset ParameterValue must not be rejected.
        var document = BuildDocument(node => node.Inputs["src"] = new Proto.ParameterValue());

        var domain = SpecProtoMapping.FromProto(document);

        domain.Nodes[0].Inputs.Should().ContainKey("src").WhoseValue.Should().BeEmpty();
    }

    [UnitTest]
    public void FromProto_NonStringParameterValue_IsRejected()
    {
        // Coercing an int64/double branch to text would let two structurally different
        // documents canonicalize to the same content hash, so it is rejected outright.
        var document = BuildDocument(node => node.Inputs["src"] = new Proto.ParameterValue { Int64Value = 5 });

        var act = () => SpecProtoMapping.FromProto(document);

        var ex = act.Should().Throw<SpecDocumentInvalidException>();
        ex.Which.PrimaryDiagnostic.Code.Should().Be(SpecDiagnosticCodes.InvalidRequestBody);
        ex.Which.PrimaryDiagnostic.Message.Should().Contain("string_value");
    }

    [UnitTest]
    public void FromProto_NonStringParameterInParameters_IsRejected()
    {
        var document = BuildDocument(node => node.Parameters["flag"] = new Proto.ParameterValue { BoolValue = true });

        var act = () => SpecProtoMapping.FromProto(document);

        act.Should().Throw<SpecDocumentInvalidException>();
    }

    private static Proto.CanonicalSpecDocument BuildDocument(Action<Proto.CanonicalSpecNode> configure)
    {
        var node = new Proto.CanonicalSpecNode
        {
            Id = "node-a",
            Kind = Proto.SpecResourceKind.Compute,
            Op = "compute.noop"
        };
        configure(node);

        var document = new Proto.CanonicalSpecDocument
        {
            GrammarVersion = "grammar/1.0",
            ProcessFamilyVersion = "family/1.0"
        };
        document.Nodes.Add(node);
        return document;
    }
}

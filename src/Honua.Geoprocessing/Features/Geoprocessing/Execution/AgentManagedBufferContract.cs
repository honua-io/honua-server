// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.ControlPlane;

namespace Honua.Geoprocessing.Execution;

// Deliberately explicit: a general analytic catalog label does not establish
// which parameters can read files, remote URLs, provider sources or staged data.
internal static class AgentManagedBufferContract
{
    internal const string Format = "honua.agent-inline-buffer/v1";
    internal const string ProcessId = "analytics.buffer-aggregate-managed";
    // Existing execution-lease intent parameters are capped at64KiB each. Keep
    // the entire base64 data URI below that original ceiling; never relax it.
    internal const long MaximumInputBytes = 32768;
    internal const long MaximumOutputBytes = 1048576;
    private const string InlinePrefix = "data:application/geo+json;base64,";
    private static readonly HashSet<string> InputNames = new(StringComparer.Ordinal)
        { "input", "distance", "unit", "dissolve", "groupByFields", "sourceCrs" };

    internal static void RequirePlan(AnalysisPlan plan, GeoprocessingExecutorOptions options)
    {
        if (plan.Steps is null || plan.Steps.Count != 1 || plan.Steps[0] is not { Kind: AnalysisPlanStepKind.Geoprocess, ProcessId: ProcessId } step
            || step.DependsOn is null || step.DependsOn.Count != 0 || step.RasterSources is null || step.RasterSources.Count != 0
            || plan.Outputs is null || plan.Outputs.Count != 1 || plan.Outputs[0] != ArtifactKind.FeatureLayer)
            throw Denied();
        RequireInputs(step.Inputs, options);
    }

    internal static void RequireJob(ExecutionJobRecord job, GeoprocessingExecutorOptions options)
    {
        var parameters = job.Spec.Parameters;
        if (job.Spec.Kind != ExecutionJobKind.Geoprocessing || job.Spec.GPServerBinding is not null || job.Audit.CustomCodeOwnerScope is not null
            || job.Spec.Backend != LocalBatchComputeBackend.BackendId
            || job.Spec.TargetKind != BatchComputeTargetKind.KubernetesJob || job.Spec.Artifact is not null
            || !string.IsNullOrEmpty(job.Spec.RuntimeProfile) && job.Spec.RuntimeProfile != RuntimeProfiles.Managed
            || !parameters.TryGetValue(ExecutionJobParameterKeys.GeoprocessingProcessDefinitions, out var process) || process != ProcessId
            || !parameters.TryGetValue(ExecutionJobParameterKeys.GeoprocessingOutputArtifactKinds, out var output) || output != nameof(ArtifactKind.FeatureLayer)
            || !parameters.TryGetValue(ExecutionJobParameterKeys.GeoprocessingPlanId, out var planId) || string.IsNullOrWhiteSpace(planId)
            || parameters.Keys.Any(key => key.StartsWith(ExecutionJobParameterKeys.GeoprocessingStepRasterSourcePrefix, StringComparison.Ordinal)
                || key.StartsWith("gpserver.", StringComparison.Ordinal) || key.StartsWith("customcode.", StringComparison.Ordinal)))
            throw Denied();
        var inputs = new Dictionary<string, string>(StringComparer.Ordinal);
        var prefix = ExecutionJobParameterKeys.GeoprocessingStepInputPrefix + "0.";
        foreach (var parameter in parameters.Where(pair => pair.Key.StartsWith(ExecutionJobParameterKeys.GeoprocessingStepInputPrefix, StringComparison.Ordinal)))
        {
            if (!parameter.Key.StartsWith(prefix, StringComparison.Ordinal)) throw Denied();
            inputs.Add(parameter.Key[prefix.Length..], parameter.Value);
        }
        RequireInputs(inputs, options);
    }

    internal static string PlanParametersFingerprint(IReadOnlyDictionary<string, string> parameters)
    {
        using var bytes = new MemoryStream();
        using (var writer = new BinaryWriter(bytes, Encoding.UTF8, leaveOpen: true))
        {
            foreach (var pair in parameters.Where(pair => IsPlanParameter(pair.Key)).OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                writer.Write(pair.Key);
                writer.Write(pair.Value);
            }
        }
        return Convert.ToHexStringLower(SHA256.HashData(bytes.GetBuffer().AsSpan(0, checked((int)bytes.Length))));
    }

    private static bool IsPlanParameter(string key)
        => key is ExecutionJobParameterKeys.GeoprocessingPlanId or ExecutionJobParameterKeys.GeoprocessingProcessDefinitions
            or ExecutionJobParameterKeys.GeoprocessingOutputArtifactKinds
            || key.StartsWith(ExecutionJobParameterKeys.GeoprocessingStepInputPrefix, StringComparison.Ordinal)
            || key.StartsWith(ExecutionJobParameterKeys.GeoprocessingStepRasterSourcePrefix, StringComparison.Ordinal);

    private static void RequireInputs(IReadOnlyDictionary<string, string> inputs, GeoprocessingExecutorOptions options)
    {
        if (inputs.Count is < 3 or > 6 || inputs.Keys.Any(key => !InputNames.Contains(key))
            || !inputs.TryGetValue("input", out var input) || input is null || !input.StartsWith(InlinePrefix, StringComparison.Ordinal)
            || input.Length > 65536 || !inputs.TryGetValue("sourceCrs", out var crs) || crs is not ("3857" or "EPSG:3857")
            || !inputs.TryGetValue("distance", out var rawDistance)
            || !double.TryParse(rawDistance, NumberStyles.Float, CultureInfo.InvariantCulture, out var distance) || !double.IsFinite(distance) || distance < 0)
            throw Denied();
        var factor = inputs.GetValueOrDefault("unit", "meters") switch
        {
            "meters" => 1d,
            "kilometers" => 1000d,
            "feet" => .3048d,
            "miles" => 1609.344d,
            _ => throw Denied()
        };
        if (!double.IsFinite(distance * factor) || distance * factor > 1000000
            || inputs.TryGetValue("dissolve", out var dissolve) && !bool.TryParse(dissolve, out _)
            || inputs.TryGetValue("groupByFields", out var groups) && (groups is null || groups.Length > 1024 || groups.Split(',').Length > 16))
            throw Denied();
        var maximum = Math.Min(MaximumInputBytes, options.MaxArtifactBytes);
        if (!FeatureCollectionArtifact.TryParseDataUri(input, out var features, out _, maximum)) throw Denied();
        var frame = FeatureArtifactSpatialReference.ReadInline(input, maximum);
        if (frame is not null && (frame.State != "known" || frame.Srid != 3857)) throw Denied();
        var vertices = features.Sum(feature => (long)(feature.Geometry?.NumPoints ?? 0));
        if (features.Count is < 1 or > 128 || vertices > Math.Min(options.MaxLayerVertices, 2048)
            || vertices * vertices > options.MaxTopologyWork
            || features.Any(feature => feature.Geometry is null || feature.Geometry.IsEmpty
                || feature.Geometry.Coordinates.Any(coordinate => !double.IsFinite(coordinate.X) || !double.IsFinite(coordinate.Y)
                    || Math.Abs(coordinate.X) > 20037509 || Math.Abs(coordinate.Y) > 20037509)))
            throw Denied();
    }

    internal static UnauthorizedAccessException Denied()
        => new("This agent process requires its exact bounded inline buffer contract and original live source authority.");
}

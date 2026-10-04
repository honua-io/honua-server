// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Geoprocessing;
using Honua.Geoprocessing.Execution;
using Honua.Protocols.GeoServices.NAServer;
using Honua.Routing.Features.Routing.Domain;
using Microsoft.Extensions.Options;

namespace Honua.Protocols.GeoServices.GPServer;

internal static partial class GPServerEndpoints
{
    private static SubmissionPlanResult BuildRoutingSubmissionPlan(
        HttpContext context, ProcessDefinition definition, string serviceId, IReadOnlyDictionary<string, string> parameters)
    {
        try
        {
            var controlsRemoved = parameters.Where(pair => !IsProtocolControlParameter(pair.Key))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
            var caps = NAServerInputCaps.FromConfiguration(context.RequestServices.GetRequiredService<IOptions<RoutingConfiguration>>().Value);
            string requestJson;
            int outputSrid;
            if (definition.ProcessId == RoutingProcessDefinitions.Route)
            {
                var request = NAServerParameterTranslation.BuildRouteSolveRequest(
                    FindRoutesWebTool.TranslateParameters(controlsRemoved), caps);
                requestJson = JsonSerializer.Serialize(request, RoutingJobJsonContext.Default.RouteSolveRequest);
                outputSrid = request.OutSrid;
            }
            else
            {
                var request = NAServerParameterTranslation.BuildServiceAreaSolveRequest(
                    RoutingGPTasks.TranslateServiceAreaParameters(controlsRemoved), caps);
                requestJson = JsonSerializer.Serialize(request, RoutingJobJsonContext.Default.ServiceAreaSolveRequest);
                outputSrid = request.OutSrid;
            }
            var plan = new AnalysisPlan
            {
                PlanId = $"gpserver-{serviceId}-routing-{Guid.NewGuid():N}",
                IntentId = $"gpserver:{serviceId}:{definition.ProcessId}",
                Steps =
                [
                    new AnalysisPlanStep
                    {
                        StepId = "routing",
                        Kind = AnalysisPlanStepKind.Geoprocess,
                        ProcessId = definition.ProcessId,
                        Inputs = new Dictionary<string, string> { ["request"] = requestJson },
                    },
                ],
                Outputs = definition.OutputArtifactKinds,
            };
            return new SubmissionPlanResult(plan, null, outputSrid);
        }
        catch (NAServerParameterTranslation.NAServerParameterException exception)
        {
            return new SubmissionPlanResult(null, exception.Message, null);
        }
    }
}

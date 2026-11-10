// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Geoprocessing.CustomCode;

namespace Honua.Geoprocessing.ProcessRelease;

public sealed class GpProcessReleaseEnvironmentResolver
{
    private readonly IExecutionJobDefinitionRegistry? _workloads;

    public GpProcessReleaseEnvironmentResolver(IExecutionJobDefinitionRegistry? workloads = null)
    {
        _workloads = workloads;
    }

    public async Task<GpProcessReleaseEnvironment> ResolveAsync(CancellationToken cancellationToken = default)
    {
        var kinds = new List<string>();
        var readyTargets = new List<GpProcessBindingTarget>();
        if (_workloads is not null)
        {
            var definitions = await _workloads.ListAsync(cancellationToken).ConfigureAwait(false);
            foreach (var definition in definitions)
            {
                if (definition.Kind != ExecutionJobKind.Geoprocessing
                    || !string.Equals(definition.RuntimeProfile, CustomCodeJobContract.RuntimeProfile, StringComparison.Ordinal))
                {
                    continue;
                }

                var kind = definition.TargetKind switch
                {
                    BatchComputeTargetKind.AwsBatch => GpProcessReleaseActivationPolicy.AwsBatchTarget,
                    BatchComputeTargetKind.KubernetesJob => GpProcessReleaseActivationPolicy.KubernetesJobTarget,
                    _ => null,
                };
                if (kind is not null && !string.IsNullOrWhiteSpace(definition.ArtifactReference))
                {
                    foreach (var parameter in definition.Parameters.Where(p =>
                        p.Key == GpProcessReleaseAdmission.AwsBatchJobDefinitionParam
                        || p.Key.StartsWith(GpProcessReleaseAdmission.AwsBatchJobDefinitionParam + ".", StringComparison.Ordinal)))
                    {
                        if (!string.IsNullOrWhiteSpace(parameter.Value))
                        {
                            readyTargets.Add(new GpProcessBindingTarget
                            {
                                Kind = kind,
                                Image = definition.ArtifactReference.Trim(),
                                JobDefinitionArn = parameter.Value.Trim(),
                            });
                        }
                    }
                }

                if (kind is not null && !kinds.Contains(kind, StringComparer.Ordinal))
                {
                    kinds.Add(kind);
                }
            }
        }

        kinds.Sort(StringComparer.Ordinal);
        return new GpProcessReleaseEnvironment
        {
            ServerContractVersion = CustomCodeJobContract.ContractVersion,
            SupportedTargetKinds = kinds,
            ReadyTargets = readyTargets,
            // ADR-0063: custom-code execution is AWS-Batch-only; there is no operator switch
            // that admits custom code to Kubernetes Jobs, so the policy refuses that target.
            CustomCodeMayUseKubernetes = false,
        };
    }
}

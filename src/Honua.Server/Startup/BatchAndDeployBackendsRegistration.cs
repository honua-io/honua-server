// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Server.Features.Infrastructure.ControlPlane;
using Honua.Server.Features.Orchestration;

namespace Honua.Server.Startup;

/// <summary>
/// Registers control-plane deploy backends (Kubernetes/AWS/Azure GitOps + revision adapters)
/// and batch-compute backends (Azure Batch, AWS Batch, Local, Kubernetes Job). The relative
/// order between deploy backends is preserved because consumers iterate <c>IEnumerable&lt;IDeployBackend&gt;</c>
/// and pick the first match by name; the same applies to <c>IBatchComputeBackend</c>.
/// </summary>
internal static class BatchAndDeployBackendsRegistration
{
    public static IServiceCollection AddHonuaBatchAndDeployBackends(this IServiceCollection services)
    {
        // Cloud SDK adapter clients used by deploy + batch backends below.
        services.AddSingleton<IAwsLambdaAliasClient, AwsSdkLambdaAliasClient>();
        services.AddSingleton<IAwsAlbClient, AwsSdkAlbClient>();
        services.AddSingleton<IAwsEcsClient, AwsSdkEcsClient>();
        services.AddSingleton<IAzureFunctionsSlotClient, AzureManagementFunctionsSlotClient>();
        services.AddSingleton<IAzureContainerAppsRevisionClient, AzureManagementContainerAppsRevisionClient>();
        services.AddSingleton<IAzureBatchClient, AzureBatchDataPlaneClient>();
        services.AddSingleton<AzureBatchComputeBackend>();
        services.AddSingleton<IBatchComputeBackend>(sp => sp.GetRequiredService<AzureBatchComputeBackend>());

        services.AddSingleton<IDeployTargetRegistry, ConfigurationDeployTargetRegistry>();
        services.AddSingleton<IExecutionJobDefinitionRegistry, ConfigurationExecutionJobDefinitionRegistry>();
        services.AddSingleton<DeployWorkflowService>();
        services.AddSingleton<IDeployTelemetrySignalEvaluator, PrometheusDeployTelemetrySignalEvaluator>();

        // Deploy backend concrete singletons. Order matters for the IEnumerable<IDeployBackend>
        // resolution below — keep the existing K8s/AWS/Azure ordering.
        services.AddSingleton<KubernetesGitOpsDeployBackend>();
        services.AddSingleton<AwsEcsGitOpsDeployBackend>();
        services.AddSingleton<AwsEcsAlbDeployBackend>();
        services.AddSingleton<AzureContainerAppsGitOpsDeployBackend>();
        services.AddSingleton<AzureContainerAppsRevisionDeployBackend>();
        services.AddSingleton<AwsLambdaGitOpsDeployBackend>();
        services.AddSingleton<AzureFunctionsGitOpsDeployBackend>();
        services.AddSingleton<IDeployBackend>(sp => sp.GetRequiredService<KubernetesGitOpsDeployBackend>());
        services.AddSingleton<IDeployBackend>(sp => sp.GetRequiredService<AwsEcsGitOpsDeployBackend>());
        services.AddSingleton<IDeployBackend>(sp => sp.GetRequiredService<AwsEcsAlbDeployBackend>());
        services.AddSingleton<IDeployBackend>(sp => sp.GetRequiredService<AzureContainerAppsGitOpsDeployBackend>());
        services.AddSingleton<IDeployBackend>(sp => sp.GetRequiredService<AzureContainerAppsRevisionDeployBackend>());
        services.AddSingleton<IDeployBackend>(sp => sp.GetRequiredService<AwsLambdaGitOpsDeployBackend>());
        services.AddSingleton<IDeployBackend>(sp => sp.GetRequiredService<AzureFunctionsGitOpsDeployBackend>());

        // Batch backends. Local fallback is registered first so it sits earlier in the enumerable.
        services.AddSingleton<LocalBatchComputeBackend>();
        services.AddSingleton<IBatchComputeBackend>(sp =>
            sp.GetRequiredService<LocalBatchComputeBackend>());

        // AWS Batch backend follows the unconditional registration pattern used by sibling AWS deploy
        // backends. Per-workload AWS Batch settings (job definition ARN, queue ARN, region, resource
        // overrides) are carried on each ExecutionJobSpec.Parameters entry via ControlPlane:ExecutionWorkloads,
        // so the adapter has no global options section it depends on. Registering unconditionally keeps
        // the backend visible to the reconciler whenever an operator targets Backend=honua-aws-batch.
        services.AddSingleton<IAwsBatchJobClient, AwsSdkBatchJobClient>();
        services.AddSingleton<AwsBatchComputeBackend>();
        services.AddSingleton<IBatchComputeBackend>(sp => sp.GetRequiredService<AwsBatchComputeBackend>());

        services.AddSingleton<IKubernetesJobClient, KubernetesJobClient>();
        services.AddSingleton<KubernetesJobBatchComputeBackend>();
        services.AddSingleton<IBatchComputeBackend>(sp =>
            sp.GetRequiredService<KubernetesJobBatchComputeBackend>());

        return services;
    }
}

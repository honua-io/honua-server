// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.ControlPlane;
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
#if !HONUA_EXCLUDE_AWS
        services.AddSingleton<IAwsLambdaAliasClient, AwsSdkLambdaAliasClient>();
        services.AddSingleton<IAwsAlbClient, AwsSdkAlbClient>();
        services.AddSingleton<IAwsEcsClient, AwsSdkEcsClient>();
#endif
#if !HONUA_EXCLUDE_AZURE
        services.AddSingleton<IAzureFunctionsSlotClient, AzureManagementFunctionsSlotClient>();
        services.AddSingleton<IAzureContainerAppsRevisionClient, AzureManagementContainerAppsRevisionClient>();
        services.AddSingleton<IAzureBatchClient, AzureBatchDataPlaneClient>();
        services.AddSingleton<AzureBatchComputeBackend>();
        services.AddSingleton<IBatchComputeBackend>(sp => sp.GetRequiredService<AzureBatchComputeBackend>());
#endif

        services.AddSingleton<IDeployTargetRegistry, ConfigurationDeployTargetRegistry>();
        services.AddSingleton<IExecutionJobDefinitionRegistry, ConfigurationExecutionJobDefinitionRegistry>();
        services.AddSingleton<DeployWorkflowService>();
        services.AddSingleton<IDeployTelemetrySignalEvaluator, PrometheusDeployTelemetrySignalEvaluator>();

        // Shared Kubernetes API request factory (in-cluster / out-of-cluster auth) consumed
        // by both the Argo Rollouts deploy client and the Kubernetes Job batch client.
        services.AddSingleton<KubernetesApiRequestFactory>();
        services.AddSingleton<IArgoRolloutsClient, ArgoRolloutsClient>();

        // Deploy backend concrete singletons. Order matters for the IEnumerable<IDeployBackend>
        // resolution below — keep the existing K8s/AWS/Azure ordering.
        services.AddSingleton<KubernetesGitOpsDeployBackend>();
        services.AddSingleton<KubernetesArgoRolloutsDeployBackend>();
#if !HONUA_EXCLUDE_AWS
        services.AddSingleton<AwsEcsGitOpsDeployBackend>();
        services.AddSingleton<AwsEcsAlbDeployBackend>();
#endif
#if !HONUA_EXCLUDE_AZURE
        services.AddSingleton<AzureContainerAppsGitOpsDeployBackend>();
        services.AddSingleton<AzureContainerAppsRevisionDeployBackend>();
#endif
#if !HONUA_EXCLUDE_AWS
        services.AddSingleton<AwsLambdaGitOpsDeployBackend>();
#endif
#if !HONUA_EXCLUDE_AZURE
        services.AddSingleton<AzureFunctionsGitOpsDeployBackend>();
#endif
        services.AddSingleton<IDeployBackend>(sp => sp.GetRequiredService<KubernetesGitOpsDeployBackend>());
        services.AddSingleton<IDeployBackend>(sp => sp.GetRequiredService<KubernetesArgoRolloutsDeployBackend>());
#if !HONUA_EXCLUDE_AWS
        services.AddSingleton<IDeployBackend>(sp => sp.GetRequiredService<AwsEcsGitOpsDeployBackend>());
        services.AddSingleton<IDeployBackend>(sp => sp.GetRequiredService<AwsEcsAlbDeployBackend>());
#endif
#if !HONUA_EXCLUDE_AZURE
        services.AddSingleton<IDeployBackend>(sp => sp.GetRequiredService<AzureContainerAppsGitOpsDeployBackend>());
        services.AddSingleton<IDeployBackend>(sp => sp.GetRequiredService<AzureContainerAppsRevisionDeployBackend>());
#endif
#if !HONUA_EXCLUDE_AWS
        services.AddSingleton<IDeployBackend>(sp => sp.GetRequiredService<AwsLambdaGitOpsDeployBackend>());
#endif
#if !HONUA_EXCLUDE_AZURE
        services.AddSingleton<IDeployBackend>(sp => sp.GetRequiredService<AzureFunctionsGitOpsDeployBackend>());
#endif

        // Batch backends. Local fallback is registered first so it sits earlier in the enumerable.
        services.AddSingleton<LocalBatchComputeBackend>();
        services.AddSingleton<IBatchComputeBackend>(sp =>
            sp.GetRequiredService<LocalBatchComputeBackend>());

        // AWS Batch backend follows the unconditional registration pattern used by sibling AWS deploy
        // backends. Per-workload AWS Batch settings (job definition ARN, queue ARN, region, resource
        // overrides) are carried on each ExecutionJobSpec.Parameters entry via ControlPlane:ExecutionWorkloads,
        // so the adapter has no global options section it depends on. Registering unconditionally keeps
        // the backend visible to the reconciler whenever an operator targets Backend=honua-aws-batch.
#if !HONUA_EXCLUDE_AWS
        services.AddSingleton<IAwsBatchJobClient, AwsSdkBatchJobClient>();
        services.AddSingleton<AwsBatchComputeBackend>();
        services.AddSingleton<IBatchComputeBackend>(sp => sp.GetRequiredService<AwsBatchComputeBackend>());
#endif

        services.AddSingleton<IKubernetesJobClient, KubernetesJobClient>();
        services.AddSingleton<KubernetesJobBatchComputeBackend>();
        services.AddSingleton<IBatchComputeBackend>(sp =>
            sp.GetRequiredService<KubernetesJobBatchComputeBackend>());

        return services;
    }
}

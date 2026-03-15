namespace Honua.Validation.Runner.Contracts;

public static class ValidationCatalog
{
    public static IReadOnlyList<ValidationTargetContract> All { get; } =
    [
        Create(
            ValidationTargetId.AwsEcs,
            "aws-ecs",
            ValidationCloud.Aws,
            "ecs",
            ValidationRegistryAuthMode.AwsEcrExecutionRole,
            "/healthz/ready",
            supportsDeployPlan: true,
            supportsMutation: true,
            requiredOutputs: ["honua_url", "db_endpoint", "control_plane_backend_name"],
            requiredEnvironmentVariables: ["HONUA_AWS_ECS_IMAGE"]),
        Create(
            ValidationTargetId.AwsLambda,
            "aws-lambda",
            ValidationCloud.Aws,
            "lambda",
            ValidationRegistryAuthMode.AwsEcrRepositoryPolicy,
            "/healthz/ready",
            supportsDeployPlan: true,
            supportsMutation: true,
            requiredOutputs: ["honua_url", "db_endpoint", "lambda_function_name"],
            requiredEnvironmentVariables: ["HONUA_AWS_SERVERLESS_IMAGE"]),
        Create(
            ValidationTargetId.AwsEks,
            "aws-eks",
            ValidationCloud.Aws,
            "kubernetes",
            ValidationRegistryAuthMode.AwsEcrExecutionRole,
            "/healthz/ready",
            supportsDeployPlan: false,
            supportsMutation: false,
            requiredOutputs: ["cluster_name", "cluster_region", "honua_url"],
            requiredEnvironmentVariables: ["HONUA_AWS_K8S_IMAGE"]),
        Create(
            ValidationTargetId.AzureAca,
            "azure-aca",
            ValidationCloud.Azure,
            "aca",
            ValidationRegistryAuthMode.AzureAcrStaticCredentials,
            "/healthz/ready",
            supportsDeployPlan: false,
            supportsMutation: false,
            requiredOutputs: ["honua_url", "resource_group_name", "control_plane_backend_name"],
            requiredEnvironmentVariables: ["HONUA_ACA_IMAGE"]),
        Create(
            ValidationTargetId.AzureFunctions,
            "azure-functions",
            ValidationCloud.Azure,
            "functions",
            ValidationRegistryAuthMode.AzureAcrStaticCredentials,
            "/healthz/ready",
            supportsDeployPlan: false,
            supportsMutation: false,
            requiredOutputs: ["honua_url", "resource_group_name", "function_app_name"],
            requiredEnvironmentVariables: ["HONUA_FUNCTIONS_IMAGE"]),
        Create(
            ValidationTargetId.AzureAks,
            "azure-aks",
            ValidationCloud.Azure,
            "kubernetes",
            ValidationRegistryAuthMode.AzureAcrManagedIdentity,
            "/healthz/ready",
            supportsDeployPlan: false,
            supportsMutation: false,
            requiredOutputs: ["cluster_name", "resource_group_name", "honua_url"],
            requiredEnvironmentVariables: ["HONUA_AKS_IMAGE"])
    ];

    public static bool TryGetByKey(string key, out ValidationTargetContract contract)
    {
        contract = All.FirstOrDefault(item => string.Equals(item.Key, key, StringComparison.OrdinalIgnoreCase))!;
        return contract is not null;
    }

    private static ValidationTargetContract Create(
        ValidationTargetId id,
        string key,
        ValidationCloud cloud,
        string runtimeKind,
        ValidationRegistryAuthMode registryAuthMode,
        string readinessPath,
        bool supportsDeployPlan,
        bool supportsMutation,
        IReadOnlyList<string> requiredOutputs,
        IReadOnlyList<string> requiredEnvironmentVariables)
    {
        return new ValidationTargetContract
        {
            Id = id,
            Key = key,
            Cloud = cloud,
            RuntimeKind = runtimeKind,
            RegistryAuthMode = registryAuthMode,
            ReadinessPath = readinessPath,
            DefaultDataTopology = ValidationDataTopologyMode.ReuseIfValidElseSeed,
            DefaultCleanupPolicy = ValidationCleanupPolicy.DestroyComputeKeepData,
            Capabilities = new ValidationTargetCapabilities
            {
                SupportsDeployPlan = supportsDeployPlan,
                SupportsMutation = supportsMutation,
                SupportsScaleCheck = runtimeKind is "ecs" or "aca" or "functions" or "kubernetes",
                SupportsUpgradeRollback = id is ValidationTargetId.AwsEcs or ValidationTargetId.AwsLambda or ValidationTargetId.AzureAca or ValidationTargetId.AzureFunctions
            },
            RequiredTerraformOutputs = requiredOutputs,
            RequiredEnvironmentVariables = requiredEnvironmentVariables
        };
    }
}

namespace Honua.Validation.Runner.Contracts;

public enum ValidationCloud
{
    Aws,
    Azure
}

public enum ValidationRegistryAuthMode
{
    AwsEcrExecutionRole,
    AwsEcrRepositoryPolicy,
    AzureAcrManagedIdentity,
    AzureAcrStaticCredentials
}

public enum ValidationDataTopologyMode
{
    Fresh,
    ReuseExisting,
    ReuseIfValidElseSeed
}

public enum ValidationCleanupPolicy
{
    DestroyAll,
    DestroyComputeKeepData,
    PreserveAll
}

public enum ValidationRunnerStatus
{
    Valid,
    Invalid,
    Error
}

public enum ValidationRunnerPhase
{
    RequestValidation,
    Unhandled
}

public enum ValidationIssueCode
{
    UnsupportedTarget,
    InvalidBaseUrl,
    InvalidBaseUrlScheme,
    MissingTerraformOutput,
    MissingEnvironmentVariable,
    UnhandledException
}

public enum ValidationTargetId
{
    AwsEcs,
    AwsLambda,
    AwsEks,
    AzureAca,
    AzureFunctions,
    AzureAks
}

public sealed record ValidationTargetCapabilities
{
    public required bool SupportsDeployPlan { get; init; }

    public required bool SupportsMutation { get; init; }

    public required bool SupportsScaleCheck { get; init; }

    public required bool SupportsUpgradeRollback { get; init; }
}

public sealed record ValidationTargetContract
{
    public required ValidationTargetId Id { get; init; }

    public required string Key { get; init; }

    public required ValidationCloud Cloud { get; init; }

    public required string RuntimeKind { get; init; }

    public required ValidationRegistryAuthMode RegistryAuthMode { get; init; }

    public required string ReadinessPath { get; init; }

    public required ValidationDataTopologyMode DefaultDataTopology { get; init; }

    public required ValidationCleanupPolicy DefaultCleanupPolicy { get; init; }

    public required ValidationTargetCapabilities Capabilities { get; init; }

    public required IReadOnlyList<string> RequiredTerraformOutputs { get; init; }

    public required IReadOnlyList<string> RequiredEnvironmentVariables { get; init; }
}

public sealed record ValidationRunnerRequest
{
    public required string Target { get; init; }

    public required string BaseUrl { get; init; }

    public ValidationDataTopologyMode DataTopology { get; init; } = ValidationDataTopologyMode.ReuseIfValidElseSeed;

    public ValidationCleanupPolicy CleanupPolicy { get; init; } = ValidationCleanupPolicy.DestroyComputeKeepData;

    public IDictionary<string, string> TerraformOutputs { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    public IDictionary<string, string> EnvironmentVariables { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);
}

public sealed record ValidationIssue
{
    public required ValidationIssueCode Code { get; init; }

    public required string Message { get; init; }

    public string? Field { get; init; }

    public string? SuggestedAction { get; init; }
}

public sealed record ValidationRunnerResult
{
    public required string Target { get; init; }

    public required ValidationRunnerStatus Status { get; init; }

    public required ValidationRunnerPhase Phase { get; init; }

    public ValidationTargetContract? Contract { get; init; }

    public IReadOnlyList<ValidationIssue> Issues { get; init; } = [];

    public IReadOnlyList<string> Errors { get; init; } = [];
}

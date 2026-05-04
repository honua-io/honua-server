// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text.RegularExpressions;
using Amazon.Lambda.Model;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;

namespace Honua.Server.Features.Infrastructure.ControlPlane;

/// <summary>
/// Built-in GitOps deploy backend for AWS Lambda targets managed by Honua.
/// </summary>
internal sealed partial class AwsLambdaGitOpsDeployBackend(
    IAwsLambdaAliasClient aliasClient,
    ILogger<AwsLambdaGitOpsDeployBackend> logger) : IDeployBackend
{
    private static readonly Regex LambdaArnRegionPattern = new("^arn:(aws[a-zA-Z-]*)?:lambda:(?<region>[^:]+):", RegexOptions.Compiled);

    public string BackendName => "honua-gitops-aws-lambda";

    public DeployTargetKind TargetKind => DeployTargetKind.AwsLambda;

    public Task<DeployBackendCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(new DeployBackendCapabilities
        {
            SupportsRollback = true,
            SupportsCancellation = false,
            SupportsTrafficShifting = true,
            RequiresOutOfBandMigrations = true,
            SupportsProgressPolling = true,
            SupportsRevisionPinning = true
        });

    public async Task<DeployPlan> PlanAsync(DeployOperationSpec spec, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(spec);
        cancellationToken.ThrowIfCancellationRequested();

        var blockingReasons = new List<string>();
        var warnings = new List<string>();

        if (string.IsNullOrWhiteSpace(spec.TargetId))
        {
            blockingReasons.Add("A target ID is required.");
        }

        if (string.IsNullOrWhiteSpace(spec.DesiredRevision))
        {
            blockingReasons.Add("A desired revision is required.");
        }
        else if (string.Equals(spec.DesiredRevision, "$LATEST", StringComparison.Ordinal))
        {
            blockingReasons.Add("AWS Lambda deploy workflows require a published function version, not $LATEST.");
        }
        else if (!long.TryParse(spec.DesiredRevision, out var desiredVersion) || desiredVersion <= 0)
        {
            blockingReasons.Add("AWS Lambda deploy workflows require desiredRevision to be a published numeric version.");
        }

        if (string.IsNullOrWhiteSpace(ResolveFunctionName(spec)))
        {
            blockingReasons.Add("A Lambda function name is required for this deploy target.");
        }

        if (string.IsNullOrWhiteSpace(ResolveAliasName(spec.Parameters)))
        {
            blockingReasons.Add("A Lambda alias name is required for this deploy target.");
        }

        if (string.IsNullOrWhiteSpace(spec.ArtifactReference))
        {
            warnings.Add("No artifact reference is configured for this target.");
        }

        if (!TryResolveCanaryWeightFraction(spec.Parameters, out var canaryWeightFraction, out var canaryWeightError))
        {
            blockingReasons.Add(canaryWeightError ?? "AWS Lambda canary weight is invalid.");
        }
        else if (canaryWeightFraction.HasValue &&
            (!spec.Parameters.TryGetValue("telemetry.connection", out var telemetryConnection) ||
             string.IsNullOrWhiteSpace(telemetryConnection)))
        {
            blockingReasons.Add("AWS Lambda canary traffic shifting requires telemetry.connection so the rollout can be promoted or rolled back automatically.");
        }

        return new DeployPlan
        {
            IsReadyToSubmit = blockingReasons.Count == 0 && !spec.RequiresApproval,
            RequiresApproval = spec.RequiresApproval,
            RequiresOutOfBandMigrations = spec.RequiresOutOfBandMigrations,
            BlockingReasons = blockingReasons,
            Warnings = warnings
        };
    }

    public async Task<DeploySubmissionResult> StartAsync(
        WorkflowOperationRecord operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var spec = operation.Deploy ?? throw new InvalidOperationException("Deploy workflow operation is missing deploy metadata.");
        cancellationToken.ThrowIfCancellationRequested();

        var functionName = ResolveFunctionName(spec);
        var aliasName = ResolveAliasName(spec.Parameters);
        var region = ResolveRegion(spec.Parameters);
        var aliasState = await aliasClient.GetAliasAsync(functionName, aliasName, region, cancellationToken).ConfigureAwait(false);
        _ = TryResolveCanaryWeightFraction(spec.Parameters, out var canaryWeightFraction, out _);
        var currentStableVersion = aliasState.FunctionVersion;

        if (canaryWeightFraction.HasValue &&
            !string.IsNullOrWhiteSpace(currentStableVersion) &&
            !string.Equals(currentStableVersion, spec.DesiredRevision, StringComparison.Ordinal))
        {
            await aliasClient.UpdateAliasAsync(
                    functionName,
                    aliasName,
                    currentStableVersion,
                    new Dictionary<string, double>(StringComparer.Ordinal)
                    {
                        [spec.DesiredRevision] = canaryWeightFraction.Value
                    },
                    region,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        else if (!string.Equals(aliasState.FunctionVersion, spec.DesiredRevision, StringComparison.Ordinal) ||
                 aliasState.AdditionalVersionWeights.Count > 0)
        {
            await aliasClient.UpdateAliasAsync(
                    functionName,
                    aliasName,
                    spec.DesiredRevision,
                    null,
                    region,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        Log.OperationSubmitted(logger, operation.OperationId, spec.TargetId, functionName, aliasName, spec.DesiredRevision);
        return new DeploySubmissionResult
        {
            Status = WorkflowOperationStatus.Submitted,
            ProviderOperationId = aliasState.AliasArn ?? $"{functionName}:{aliasName}",
            ObservedRevision = aliasState.FunctionVersion,
            Message = canaryWeightFraction.HasValue && !string.IsNullOrWhiteSpace(currentStableVersion) && !string.Equals(currentStableVersion, spec.DesiredRevision, StringComparison.Ordinal)
                ? $"Lambda alias '{aliasName}' is routing {Math.Round(canaryWeightFraction.Value * 100, 3):0.###}% of traffic to published version '{spec.DesiredRevision}'."
                : $"Lambda alias '{aliasName}' is moving to published version '{spec.DesiredRevision}'."
        };
    }

    public async Task<DeployObservation> ObserveAsync(
        WorkflowOperationRecord operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var spec = operation.Deploy ?? throw new InvalidOperationException("Deploy workflow operation is missing deploy metadata.");
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var functionName = ResolveFunctionName(spec);
            var aliasName = ResolveAliasName(spec.Parameters);
            var region = ResolveRegion(spec.Parameters);
            var aliasState = await aliasClient.GetAliasAsync(functionName, aliasName, region, cancellationToken).ConfigureAwait(false);
            var hasWeightedTraffic = aliasState.AdditionalVersionWeights.Count > 0;
            _ = TryResolveCanaryWeightFraction(spec.Parameters, out var desiredWeightFraction, out _);
            var routesDesiredRevision = aliasState.AdditionalVersionWeights.TryGetValue(spec.DesiredRevision, out var currentCanaryWeight);

            if (operation.Status == WorkflowOperationStatus.RollbackRequested)
            {
                var rollbackVersion = spec.CurrentRevision;
                if (!string.IsNullOrWhiteSpace(rollbackVersion) &&
                    string.Equals(aliasState.FunctionVersion, rollbackVersion, StringComparison.Ordinal) &&
                    !hasWeightedTraffic)
                {
                    return new DeployObservation
                    {
                        Status = WorkflowOperationStatus.RolledBack,
                        ProviderOperationId = aliasState.AliasArn ?? operation.ProviderOperationId,
                        ObservedRevision = aliasState.FunctionVersion,
                        Message = $"Lambda alias '{aliasName}' now points to rollback version '{rollbackVersion}'."
                    };
                }

                return new DeployObservation
                {
                    Status = WorkflowOperationStatus.RollbackRequested,
                    ProviderOperationId = aliasState.AliasArn ?? operation.ProviderOperationId,
                    ObservedRevision = aliasState.FunctionVersion,
                    Message = $"Lambda alias '{aliasName}' is still converging on rollback version '{rollbackVersion ?? "unknown"}'."
                };
            }

            if (string.Equals(aliasState.FunctionVersion, spec.DesiredRevision, StringComparison.Ordinal) && !hasWeightedTraffic)
            {
                return new DeployObservation
                {
                    Status = WorkflowOperationStatus.Succeeded,
                    ProviderOperationId = aliasState.AliasArn ?? operation.ProviderOperationId,
                    ObservedRevision = aliasState.FunctionVersion,
                    Message = $"Lambda alias '{aliasName}' now points to published version '{spec.DesiredRevision}'."
                };
            }

            if (desiredWeightFraction.HasValue &&
                !string.IsNullOrWhiteSpace(spec.CurrentRevision) &&
                string.Equals(aliasState.FunctionVersion, spec.CurrentRevision, StringComparison.Ordinal) &&
                routesDesiredRevision &&
                Math.Abs(currentCanaryWeight - desiredWeightFraction.Value) <= 0.0001d)
            {
                return new DeployObservation
                {
                    Status = WorkflowOperationStatus.Reconciling,
                    ProviderOperationId = aliasState.AliasArn ?? operation.ProviderOperationId,
                    ObservedRevision = aliasState.FunctionVersion,
                    PromotionRecommended = true,
                    Message = $"Lambda alias '{aliasName}' is holding stable version '{spec.CurrentRevision}' while routing {Math.Round(currentCanaryWeight * 100, 3):0.###}% of traffic to canary version '{spec.DesiredRevision}'."
                };
            }

            return new DeployObservation
            {
                Status = WorkflowOperationStatus.Reconciling,
                ProviderOperationId = aliasState.AliasArn ?? operation.ProviderOperationId,
                ObservedRevision = aliasState.FunctionVersion,
                Message = hasWeightedTraffic
                    ? $"Lambda alias '{aliasName}' still has weighted traffic configured while converging to version '{spec.DesiredRevision}'."
                    : $"Lambda alias '{aliasName}' is still converging to version '{spec.DesiredRevision}'."
            };
        }
        catch (ResourceNotFoundException ex)
        {
            Log.AliasLookupFailed(logger, operation.OperationId, spec.TargetId, ex.Message);
            return new DeployObservation
            {
                Status = WorkflowOperationStatus.Failed,
                ProviderOperationId = operation.ProviderOperationId,
                ObservedRevision = spec.CurrentRevision,
                Message = ex.Message
            };
        }
    }

    public async Task<DeployObservation> PromoteAsync(
        WorkflowOperationRecord operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var spec = operation.Deploy ?? throw new InvalidOperationException("Deploy workflow operation is missing deploy metadata.");
        cancellationToken.ThrowIfCancellationRequested();

        var functionName = ResolveFunctionName(spec);
        var aliasName = ResolveAliasName(spec.Parameters);
        var region = ResolveRegion(spec.Parameters);
        var updatedAlias = await aliasClient.UpdateAliasAsync(
                functionName,
                aliasName,
                spec.DesiredRevision,
                null,
                region,
                cancellationToken)
            .ConfigureAwait(false);

        var hasWeightedTraffic = updatedAlias.AdditionalVersionWeights.Count > 0;
        return new DeployObservation
        {
            Status = string.Equals(updatedAlias.FunctionVersion, spec.DesiredRevision, StringComparison.Ordinal) && !hasWeightedTraffic
                ? WorkflowOperationStatus.Succeeded
                : WorkflowOperationStatus.Reconciling,
            ProviderOperationId = updatedAlias.AliasArn ?? operation.ProviderOperationId,
            ObservedRevision = updatedAlias.FunctionVersion,
            Message = hasWeightedTraffic
                ? $"Lambda alias '{aliasName}' is still draining weighted canary traffic before full promotion to version '{spec.DesiredRevision}'."
                : $"Lambda alias '{aliasName}' has been promoted to published version '{spec.DesiredRevision}'."
        };
    }

    public async Task<DeployObservation> RollbackAsync(
        WorkflowOperationRecord operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var spec = operation.Deploy ?? throw new InvalidOperationException("Deploy workflow operation is missing deploy metadata.");
        cancellationToken.ThrowIfCancellationRequested();

        var rollbackVersion = spec.CurrentRevision;
        if (string.IsNullOrWhiteSpace(rollbackVersion))
        {
            return new DeployObservation
            {
                Status = WorkflowOperationStatus.ManualInterventionRequired,
                ProviderOperationId = operation.ProviderOperationId,
                ObservedRevision = spec.CurrentRevision,
                Message = "Rollback requires a previously observed Lambda version, but none was captured for this operation."
            };
        }

        var functionName = ResolveFunctionName(spec);
        var aliasName = ResolveAliasName(spec.Parameters);
        var region = ResolveRegion(spec.Parameters);
        var updatedAlias = await aliasClient.UpdateAliasAsync(
                functionName,
                aliasName,
                rollbackVersion,
                null,
                region,
                cancellationToken)
            .ConfigureAwait(false);

        Log.RollbackRequested(logger, operation.OperationId, spec.TargetId, functionName, aliasName, rollbackVersion);
        return new DeployObservation
        {
            Status = WorkflowOperationStatus.RollbackRequested,
            ProviderOperationId = updatedAlias.AliasArn ?? operation.ProviderOperationId,
            ObservedRevision = updatedAlias.FunctionVersion,
            Message = $"Lambda alias '{aliasName}' is moving back to published version '{rollbackVersion}'."
        };
    }

    private static string ResolveFunctionName(DeployOperationSpec spec)
        => GetParameter(spec.Parameters, "aws.lambda.function_name")
           ?? GetParameter(spec.Parameters, "lambda.function_name")
           ?? GetParameter(spec.Parameters, "lambda.alias_function_name")
           ?? spec.TargetName;

    private static string ResolveAliasName(IReadOnlyDictionary<string, string> parameters)
        => GetParameter(parameters, "aws.lambda.alias_name")
           ?? GetParameter(parameters, "lambda.alias_name")
           ?? GetParameter(parameters, "lambda.alias")
           ?? "live";

    private static string? ResolveRegion(IReadOnlyDictionary<string, string> parameters)
    {
        var explicitRegion = GetParameter(parameters, "aws.region")
            ?? GetParameter(parameters, "lambda.region");
        if (!string.IsNullOrWhiteSpace(explicitRegion))
        {
            return explicitRegion;
        }

        var resourceId = GetParameter(parameters, "target.resource_id")
            ?? GetParameter(parameters, "lambda.function_arn")
            ?? GetParameter(parameters, "lambda.alias_arn");
        if (string.IsNullOrWhiteSpace(resourceId))
        {
            return null;
        }

        var match = LambdaArnRegionPattern.Match(resourceId);
        return match.Success ? match.Groups["region"].Value : null;
    }

    private static string? GetParameter(IReadOnlyDictionary<string, string> parameters, string key)
        => parameters.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : null;

    private static bool TryResolveCanaryWeightFraction(
        IReadOnlyDictionary<string, string> parameters,
        out double? fraction,
        out string? error)
    {
        var rawValue = GetParameter(parameters, "aws.lambda.canary_weight_percentage")
            ?? GetParameter(parameters, "lambda.canary_weight_percentage")
            ?? GetParameter(parameters, "deployment.canary_weight_percentage");
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            fraction = null;
            error = null;
            return true;
        }

        if (!double.TryParse(rawValue, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var parsedPercentage))
        {
            fraction = null;
            error = "AWS Lambda canary weight must be a numeric percentage between 0 and 100.";
            return false;
        }

        if (parsedPercentage <= 0 || parsedPercentage >= 100)
        {
            fraction = null;
            error = "AWS Lambda canary weight must be greater than 0 and less than 100 percent.";
            return false;
        }

        fraction = parsedPercentage / 100d;
        error = null;
        return true;
    }

    private static partial class Log
    {
        [LoggerMessage(9030, LogLevel.Information, "Submitted Lambda deploy workflow operation {OperationId} for target {TargetId} ({FunctionName}:{AliasName}) to version {DesiredVersion}")]
        public static partial void OperationSubmitted(ILogger logger, string operationId, string targetId, string functionName, string aliasName, string desiredVersion);

        [LoggerMessage(9031, LogLevel.Warning, "Rollback requested for Lambda workflow operation {OperationId} targeting {TargetId} ({FunctionName}:{AliasName}) to version {RollbackVersion}")]
        public static partial void RollbackRequested(ILogger logger, string operationId, string targetId, string functionName, string aliasName, string rollbackVersion);

        [LoggerMessage(9032, LogLevel.Warning, "Lambda alias lookup failed for workflow operation {OperationId} targeting {TargetId}: {ErrorMessage}")]
        public static partial void AliasLookupFailed(ILogger logger, string operationId, string targetId, string errorMessage);
    }
}

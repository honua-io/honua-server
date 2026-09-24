// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.ControlPlane;
using Honua.Core.Features.ControlPlane.Domain;
using Microsoft.Extensions.Logging.Abstractions;

if (args.Length != 6 || args[0] is not ("shift" or "rollback"))
{
    throw new ArgumentException("Expected: shift|rollback function alias previous candidate region");
}

var functionName = args[1];
if (!functionName.StartsWith("honua-cert-cert-", StringComparison.Ordinal))
{
    throw new InvalidOperationException("Refusing alias mutation outside the standing cert namespace.");
}

var client = new AwsSdkLambdaAliasClient();
var backend = new AwsLambdaGitOpsDeployBackend(client, NullLogger<AwsLambdaGitOpsDeployBackend>.Instance);
var parameters = new Dictionary<string, string>
{
    ["aws.lambda.function_name"] = functionName,
    ["aws.lambda.alias_name"] = args[2],
    ["aws.region"] = args[5]
};
var spec = new DeployOperationSpec
{
    TargetId = "lambda-certification",
    TargetName = functionName,
    TargetKind = DeployTargetKind.AwsLambda,
    Backend = backend.BackendName,
    Environment = "cert",
    CurrentRevision = args[3],
    DesiredRevision = args[4],
    Parameters = parameters
};
var plan = await backend.PlanAsync(spec);
if (!plan.IsReadyToSubmit)
{
    throw new InvalidOperationException("Certification deploy plan is not ready.");
}

var state = await client.GetAliasAsync(functionName, args[2], args[5]);
if (state.AdditionalVersionWeights.Count != 0 ||
    (state.FunctionVersion != args[3] && state.FunctionVersion != args[4]))
{
    throw new InvalidOperationException("Standing alias drifted; refusing to overwrite unrelated state.");
}

var rollback = args[0] == "rollback";
var operation = new WorkflowOperationRecord
{
    OperationId = "lambda-certification",
    Kind = WorkflowOperationKind.Deploy,
    Status = rollback ? WorkflowOperationStatus.RollbackRequested : WorkflowOperationStatus.Submitted,
    CreatedAt = DateTimeOffset.UtcNow,
    UpdatedAt = DateTimeOffset.UtcNow,
    Deploy = spec
};
if (rollback)
{
    await backend.RollbackAsync(operation);
}
else
{
    await backend.StartAsync(operation);
}

var observed = await backend.ObserveAsync(operation);
if (rollback)
{
    // Alias convergence is not a certified rollback. This driver does not attach a data-plane
    // probe, so the backend must stay non-terminal (or fail) instead of reporting RolledBack.
    if (observed.Status == WorkflowOperationStatus.RolledBack)
    {
        throw new InvalidOperationException(
            "Refusing to certify Lambda rollback: alias convergence alone reported RolledBack without restored readiness and a functional query.");
    }

    throw new InvalidOperationException(
        $"Lambda rollback is not certified (honua-server#3892). Alias observation status is '{observed.Status}', version '{observed.ObservedRevision ?? "<none>"}'. A live receipt still has to prove readiness and a functional query.");
}

if (observed.Status != WorkflowOperationStatus.Succeeded || observed.ObservedRevision != args[4])
{
    throw new InvalidOperationException("Deploy backend did not converge to the requested version.");
}

Console.WriteLine(JsonSerializer.Serialize(new { version = observed.ObservedRevision, status = observed.Status.ToString() }));

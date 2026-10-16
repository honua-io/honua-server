// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Amazon.Batch.Model;
using Honua.ControlPlane;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.ControlPlane.Domain;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

public sealed class CustomCodeBatchBoundaryTests
{
    [UnitTest]
    public void CredentialFreeFargateDefinition_AcceptsProviderOnlyExecutionRole()
        => AwsSdkBatchJobClient.ValidateCredentialFreeCustomCodeDefinition(SafeDefinition(), []);

    [UnitTheory]
    [InlineData("task-role")]
    [InlineData("ec2")]
    [InlineData("secret")]
    [InlineData("root")]
    [InlineData("static-key")]
    [InlineData("metadata-uri")]
    [InlineData("volume")]
    public void UnsafeCustomCodeDefinition_IsRejected(string defect)
    {
        var definition = SafeDefinition();
        switch (defect)
        {
            case "task-role": definition.ContainerProperties.JobRoleArn = "arn:aws:iam::123:role/shared"; break;
            case "ec2": definition.PlatformCapabilities = ["EC2"]; break;
            case "secret": definition.ContainerProperties.Secrets = [new Secret { Name = "TOKEN", ValueFrom = "secret" }]; break;
            case "root": definition.ContainerProperties.User = "0"; break;
            case "static-key": definition.ContainerProperties.Environment = [new KeyValuePair { Name = "AWS_SECRET_ACCESS_KEY", Value = "fake" }]; break;
            case "metadata-uri": definition.ContainerProperties.Environment = [new KeyValuePair { Name = "AWS_CONTAINER_CREDENTIALS_FULL_URI", Value = "http://metadata.invalid" }]; break;
            case "volume": definition.ContainerProperties.Volumes = [new Volume { Name = "host" }]; break;
        }
        Assert.Throws<InvalidOperationException>(() => AwsSdkBatchJobClient.ValidateCredentialFreeCustomCodeDefinition(definition, []));
    }

    [UnitTest]
    public void CredentialOverride_IsRejectedEvenForSafeDefinition()
        => Assert.Throws<InvalidOperationException>(() => AwsSdkBatchJobClient.ValidateCredentialFreeCustomCodeDefinition(
            SafeDefinition(), [new AwsBatchEnvironmentOverride("AWS_ACCESS_KEY_ID", "fake")]));

    [UnitTest]
    public async Task Dispatch_RotatesAttemptCapabilityAndPersistsExactDispatchedToken()
    {
        var client = new StubAwsBatchJobClient();
        var issuer = Substitute.For<IScopedJobTokenIssuer>();
        issuer.IssueAsync(Arg.Any<ScopedJobTokenRequest>(), Arg.Any<CancellationToken>())
            .Returns(call => new ScopedJobTokenIssuance("capability-" + call.Arg<ScopedJobTokenRequest>().Attempt, DateTimeOffset.UtcNow.AddHours(1)));
        var backend = new AwsBatchComputeBackend(client, Options.Create(new AwsBatchExecutionOptions()),
            NullLogger<AwsBatchComputeBackend>.Instance, issuer);
        var owner = new CustomCodeOwnerScope("owner", "tenant", [], [], []);
        var job = AwsBatchComputeBackendTests.CreateJob() with { Audit = new OperationAuditInfo { CustomCodeOwnerScope = owner } };
        var prepared = await backend.PrepareSubmissionAsync(job, CancellationToken.None);
        var firstParameters = job.Spec.Parameters.Concat(prepared!).ToDictionary(pair => pair.Key, pair => pair.Value);
        var first = await backend.StartAsync(job with { Spec = job.Spec with { Parameters = firstParameters } });
        Assert.Equal("1", first.ResolvedParameters!["env.HONUA_JOB_ATTEMPT"]);
        Assert.Equal("capability-1", first.ResolvedParameters["env.HONUA_JOB_TOKEN"]);
        Assert.True(client.LastSubmission!.RequireCredentialFreeCustomCode);
        Assert.Equal(1, client.LastSubmission.RetryAttempts);
        Assert.Contains(client.LastSubmission.EnvironmentOverrides, entry => entry.Name == "HONUA_JOB_ID" && entry.Value == job.OperationId);
        var retryJob = job with { AttemptCount = 1 };
        prepared = await backend.PrepareSubmissionAsync(retryJob, CancellationToken.None);
        var retryParameters = job.Spec.Parameters.Concat(prepared!).ToDictionary(pair => pair.Key, pair => pair.Value);
        var retry = await backend.StartAsync(retryJob with { Spec = job.Spec with { Parameters = retryParameters } });
        Assert.Equal("2", retry.ResolvedParameters!["env.HONUA_JOB_ATTEMPT"]);
        Assert.Equal("capability-2", retry.ResolvedParameters["env.HONUA_JOB_TOKEN"]);
        await issuer.Received(1).IssueAsync(Arg.Is<ScopedJobTokenRequest>(request =>
            request.Attempt == 2 && request.JobId == job.OperationId && request.TenantId == "tenant"), Arg.Any<CancellationToken>());
    }

    private static JobDefinition SafeDefinition() => new()
    {
        PlatformCapabilities = ["FARGATE"],
        ContainerProperties = new ContainerProperties { User = "1001:1001", ExecutionRoleArn = "arn:aws:iam::123:role/execution" }
    };
}

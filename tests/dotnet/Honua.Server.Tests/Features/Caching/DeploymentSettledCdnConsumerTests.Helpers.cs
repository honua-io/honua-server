// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

#if !HONUA_EXCLUDE_AWS
using System.Text.Json;
using Honua.ControlPlane;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Observability.Domain;
using Honua.Infrastructure.SharedCaching;
using Honua.Server.Features.Infrastructure.Caching;
using Honua.TestKit;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using NSubstitute;

namespace Honua.Server.Tests.Features.Caching;

public sealed partial class DeploymentSettledCdnConsumerTests
{
    private const string Cluster = "arn:aws:ecs:us-west-2:123456789012:cluster/test";

    private static OpsExternalEvent Event(string id = "event", string deployment = "ecs-svc/new",
        string service = "server", string eventName = "SERVICE_DEPLOYMENT_COMPLETED")
        => new(id, DateTimeOffset.UtcNow, JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["source"] = "aws.ecs", ["detail-type"] = "ECS Deployment State Change", ["region"] = "us-west-2",
            ["resources"] = new[] { $"arn:aws:ecs:us-west-2:123456789012:service/test/{service}" },
            ["detail"] = new { eventName, deploymentId = deployment }
        }));

    private static AwsEcsServiceState Settled() => new()
    {
        ServiceName = "server", Status = "ACTIVE", RunningCount = 2, DesiredCount = 2, TaskDefinitionArn = "task:2",
        Deployments = [new AwsEcsDeploymentState { DeploymentId = "ecs-svc/new", Status = "PRIMARY",
            TaskDefinitionArn = "task:2", RolloutState = "COMPLETED", RunningCount = 2, DesiredCount = 2 }]
    };

    private static IAwsEcsClient Ecs(AwsEcsServiceState state)
    {
        var client = Substitute.For<IAwsEcsClient>();
        client.DescribeServiceAsync(default!, default!, default, default).ReturnsForAnyArgs(state);
        return client;
    }

    private static ICdnInvalidator Cdn(params CdnInvalidationOutcome[] outcomes)
    {
        var cdn = Substitute.For<ICdnInvalidator>();
        var results = outcomes.Select(outcome => new CdnInvalidationResult(outcome)).ToArray();
        cdn.InvalidateAsync(default!, default).ReturnsForAnyArgs(results[0], results.Skip(1).ToArray());
        return cdn;
    }

    private static DeploymentSettledCdnConsumer Host(JobStoreDatabase database, IAwsEcsClient ecs, ICdnInvalidator cdn,
        Action<DeployTargetOptions>? configure = null)
    {
        var target = new DeployTargetOptions
        {
            TargetKind = DeployTargetKind.AwsEcs, Backend = "honua-aws-ecs-alb", TargetName = "server",
            Parameters = new() { ["aws.region"] = "us-west-2", ["aws.ecs.cluster"] = Cluster }
        };
        configure?.Invoke(target);
        return new(database, ecs, new CdnInvalidationService(cdn, NullLogger<CdnInvalidationService>.Instance),
            Options.Create(new ControlPlaneOptions { DeployTargets = [target] }));
    }

    private async Task InSchemaAsync(Func<JobStoreDatabase, Task> test)
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(DeploymentSettledCdnConsumerTests));
        try
        {
            var sql = await File.ReadAllTextAsync(RepositoryPaths.Resolve(
                "src", "Honua.Server", "Migrations", "163_CreateCdnDeploymentPurges.sql"));
            await using var command = fixture.DataSource.CreateCommand(sql.Replace("$HonuaSchema$", $"\"{schema}\"", StringComparison.Ordinal));
            await command.ExecuteNonQueryAsync();
            await test(new JobStoreDatabase(fixture.DataSource, schema));
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    private static Task<long> CountAsync(JobStoreDatabase database)
        => database.ExecuteAsync(async (connection, _, token) =>
        {
            await using var command = new NpgsqlCommand($"SELECT count(*) FROM {database.Table("cdn_deployment_purges")}", connection);
            return (long)(await command.ExecuteScalarAsync(token))!;
        }, StatementRepeat.SafeToRepeat, default);
}
#endif

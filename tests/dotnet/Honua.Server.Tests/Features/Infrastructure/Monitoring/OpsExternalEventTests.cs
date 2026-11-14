// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Observability.Domain;
using Honua.Infrastructure.Monitoring;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Infrastructure.Monitoring;

[Protocol(TestProtocols.TestQuality)]
public sealed class OpsExternalEventTests
{
    [Theory]
    [InlineData("aws.ecs", "ECS Task State Change", "{\"lastStatus\":\"STOPPED\",\"containers\":[{\"exitCode\":137}]}", "ecs-task-oom")]
    [InlineData("aws.ecs", "ECS Task State Change", "{\"lastStatus\":\"STOPPED\",\"stoppedReason\":\"CannotPullContainerError: denied\"}", "image-pull-failed")]
    [InlineData("aws.ecs", "ECS Deployment State Change", "{\"eventName\":\"SERVICE_DEPLOYMENT_FAILED\"}", "ecs-deployment-failed")]
    [InlineData("aws.ecs", "ECS Deployment State Change", "{\"eventName\":\"SERVICE_DEPLOYMENT_COMPLETED\"}", "ecs-deployment-completed")]
    [InlineData("aws.backup", "Backup Job State Change", "{\"state\":\"FAILED\"}", "backup-job-failed")]
    [InlineData("aws.backup", "Copy Job State Change", "{\"state\":\"FAILED\"}", "backup-copy-failed")]
    [InlineData("aws.rds", "RDS DB Instance Event", "{\"EventCategories\":[\"maintenance\"]}", "rds-maintenance")]
    [InlineData("aws.batch", "Batch Job State Change", "{\"status\":\"FAILED\"}", "gp-job-failed")]
    [InlineData("aws.acm", "ACM Certificate Approaching Expiration", "{}", "certificate-expiring")]
    [InlineData("aws.health", "AWS Health Event", "{}", "aws-health-event")]
    [InlineData("aws.ce", "Anomaly Detected", "{}", "cost-anomaly")]
    [InlineData("aws.cloudwatch", "CloudWatch Alarm State Change", "{\"state\":{\"value\":\"ALARM\"}}", "cloudwatch-alarm")]
    [Operation(Operations.TestInfrastructure)]
    public void Mapping_HasStableIdentityAndPlainLanguageTemplate(string source, string type, string detail, string rule)
    {
        var body = Envelope(source, type, detail);
        var finding = OpsExternalEventMapper.Map(body);
        finding.Should().NotBeNull();
        finding!.Rule.Should().Be(rule);
        finding.Title.Should().NotBeNullOrWhiteSpace();
        finding.Explanation.Should().NotBeNullOrWhiteSpace();
        finding.Id.Should().Be(OpsExternalEventMapper.Map(body)!.Id);
        finding.RecommendedAction.Should().BeNull();
    }

    internal static string Envelope(string source, string type, string detail) =>
        $$"""{"id":"7a92fa24-ff98-4aaa-a495-952e7802d201","source":"{{source}}","detail-type":"{{type}}","time":"2026-11-14T00:00:00Z","resources":["arn:aws:ecs:eu-north-1:123456789012:service/stack/server"],"detail":{{detail}}} """;
}

// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Text;
using System.Text.Json;
using Honua.Core.Features.Observability.Domain;

namespace Honua.Infrastructure.Monitoring;

internal static class OpsExternalEventMapper
{
    internal static OpsExternalEvent Parse(string body)
    {
        if (Encoding.UTF8.GetByteCount(body) > 262144)
        {
            throw new JsonException("Ops event exceeds the 256 KiB limit.");
        }

        return ParseEnvelope(body);
    }

    private static OpsExternalEvent ParseEnvelope(string body)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        var id = Text(root, "id");
        var timestamp = Text(root, "time");
        if (id.Length is 0 or > 128 || Text(root, "source").Length == 0 ||
            Text(root, "detail-type").Length == 0 ||
            !root.TryGetProperty("detail", out var detail) || detail.ValueKind != JsonValueKind.Object ||
            !DateTimeOffset.TryParse(timestamp, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var occurredAt))
        {
            throw new JsonException("Ops event requires an id, source, detail-type, UTC time and object detail.");
        }

        return new OpsExternalEvent(id, occurredAt.ToUniversalTime(), body);
    }

    internal static OpsFinding? Map(string body)
    {
        var externalEvent = ParseEnvelope(body);
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        var detail = root.GetProperty("detail");
        var template = SelectTemplate(root, detail);
        if (template is null)
        {
            return null;
        }

        var resource = Resource(root, detail);
        return new OpsFinding
        {
            Id = OpsFindingId.Create(template.Rule, externalEvent.Id),
            Rule = template.Rule,
            Severity = template.Severity,
            Title = template.Title,
            Explanation = template.Explanation,
            DetectedAt = externalEvent.OccurredAt,
            Subject = new OpsFindingSubject { TargetId = resource, OperationId = Text(detail, "taskArn") is { Length: > 0 } taskArn ? taskArn : null },
            EvidenceRefs = [$"aws-event:{externalEvent.Id}"],
        };
    }

    internal static IReadOnlyList<OpsFinding> MapRecent(IReadOnlyList<OpsExternalEvent> events, DateTimeOffset now)
    {
        var mapped = events.DistinctBy(e => e.Id).Where(e => e.OccurredAt <= now)
            .Select(e => Map(e.Body)).OfType<OpsFinding>().ToArray();
        var findings = mapped.Where(f => StateFamily(f) is null).ToList();
        findings.AddRange(mapped.Where(f => StateFamily(f) is not null)
            .GroupBy(f => (Family: StateFamily(f), f.Subject.TargetId))
            .Select(g => g.OrderByDescending(f => f.DetectedAt).ThenBy(f => f.Id, StringComparer.Ordinal).First()));
        var crashes = findings.Where(f => f.Rule is "ecs-task-stopped" or "ecs-task-oom")
            .Where(f => f.DetectedAt >= now - TimeSpan.FromMinutes(15) && f.DetectedAt <= now)
            .Where(f => !string.IsNullOrWhiteSpace(f.Subject.OperationId))
            .DistinctBy(f => (f.Subject.TargetId, f.Subject.OperationId))
            .GroupBy(f => f.Subject.TargetId, StringComparer.Ordinal);
        foreach (var group in crashes.Where(g => g.Count() >= 3))
        {
            findings.Add(new OpsFinding
            {
                Id = OpsFindingId.Create("ecs-task-crash-loop", group.Key ?? "unknown"),
                Rule = "ecs-task-crash-loop",
                Severity = OpsFindingSeverity.Critical,
                Title = "Server tasks are repeatedly stopping",
                Explanation = "At least three tasks stopped with errors in 15 minutes. Check the active deployment and stopped-task reasons before changing capacity.",
                DetectedAt = group.Max(f => f.DetectedAt),
                Subject = new OpsFindingSubject { TargetId = group.Key },
                EvidenceRefs = group.SelectMany(f => f.EvidenceRefs).ToArray(),
            });
        }

        return findings;
    }

    private static string? StateFamily(OpsFinding finding) => finding.Rule switch
    {
        var rule when rule.StartsWith("cloudwatch-alarm", StringComparison.Ordinal) => "cloudwatch-alarm",
        var rule when rule.StartsWith("ecs-deployment-", StringComparison.Ordinal) => "ecs-deployment",
        _ => null,
    };

    private static Template? SelectTemplate(JsonElement root, JsonElement detail)
    {
        var source = Text(root, "source");
        var type = Text(root, "detail-type");
        return source switch
        {
            "aws.ecs" when type == "ECS Task State Change" => TaskTemplate(detail),
            "aws.ecs" when type == "ECS Deployment State Change" => DeploymentTemplate(detail),
            "aws.backup" when IsFailure(Text(detail, "state")) => type switch
            {
                "Backup Job State Change" => Warning("backup-job-failed", "A backup did not finish", "AWS Backup reported a failed, expired or aborted backup. Check the backup job and verify that a recent recovery point exists."),
                "Copy Job State Change" => Warning("backup-copy-failed", "A backup copy did not finish", "The backup copy failed. The source recovery point may still exist; check the destination vault and copy-job permissions."),
                _ => null,
            },
            "aws.batch" when type == "Batch Job State Change" && Text(detail, "status") == "FAILED" =>
                Warning("gp-job-failed", "A processing job failed", "AWS Batch reported a failed job. Check the job's stopped reason and input before retrying; no automatic retry was requested."),
            "aws.rds" => RdsTemplate(detail),
            "aws.acm" when type == "ACM Certificate Approaching Expiration" =>
                Warning("certificate-expiring", "A TLS certificate is approaching expiration", "Check the certificate's renewal status and DNS validation. Imported or customer-supplied certificates do not automatically renew through ACM."),
            "aws.health" when type == "AWS Health Event" =>
                Warning("aws-health-event", "AWS reported a service health event", "Review the AWS Health event for affected resources, timing and any required operator action."),
            "aws.ce" when type.Contains("Anomaly", StringComparison.OrdinalIgnoreCase) =>
                Warning("cost-anomaly", "AWS detected unusual spending", "Review the cost anomaly and affected services before approving any capacity or commitment changes."),
            "aws.cloudwatch" when type == "CloudWatch Alarm State Change" => AlarmTemplate(detail),
            _ => null,
        };
    }

    private static Template? TaskTemplate(JsonElement detail)
    {
        if (Text(detail, "lastStatus") != "STOPPED" ||
            Text(detail, "stopCode") is "UserInitiated" or "ServiceSchedulerInitiated" or "SpotInterruption" or "TerminationNotice")
        {
            return null;
        }

        var reason = Text(detail, "stoppedReason");
        var containers = detail.TryGetProperty("containers", out var array) && array.ValueKind == JsonValueKind.Array
            ? array.EnumerateArray().ToArray() : [];
        if (reason.Contains("CannotPullContainerError", StringComparison.OrdinalIgnoreCase) ||
            containers.Any(c => Text(c, "reason").Contains("CannotPullContainerError", StringComparison.OrdinalIgnoreCase)))
        {
            return Warning("image-pull-failed", "A server image could not be pulled", "Check the image digest, registry access and task execution-role permissions. Changing task capacity will not repair registry credentials.");
        }

        if (reason.Contains("OutOfMemory", StringComparison.OrdinalIgnoreCase) || containers.Any(c => Text(c, "reason").Contains("OutOfMemory", StringComparison.OrdinalIgnoreCase)))
        {
            return Warning("ecs-task-oom", "A server task ran out of memory", "Check task memory and workload pressure. A memory increase changes cost and requires operator approval.");
        }

        return containers.Any(c => ExitCode(c) is { } code && code != 0)
            ? Warning("ecs-task-stopped", "A server task stopped with an error", "Inspect the stopped-task reason and active deployment. Repeated failures may indicate a crash loop.")
            : null;
    }

    private static Template? DeploymentTemplate(JsonElement detail) => Text(detail, "eventName") switch
    {
        "SERVICE_DEPLOYMENT_FAILED" => new("ecs-deployment-failed", "A server deployment failed", "Inspect the ECS deployment and circuit-breaker rollback. Verify which task definition is serving before attempting another deployment.") { Severity = OpsFindingSeverity.Critical },
        "SERVICE_DEPLOYMENT_COMPLETED" => new("ecs-deployment-completed", "A server deployment completed", "ECS reports the deployment completed. Confirm the expected release is serving and that post-deployment checks pass.") { Severity = OpsFindingSeverity.Info },
        "SERVICE_DEPLOYMENT_ROLLBACK_COMPLETED" => new("ecs-deployment-rollback-completed", "A server deployment rollback completed", "ECS reports the rollback completed. Confirm the restored task definition is healthy before retrying the update.") { Severity = OpsFindingSeverity.Info },
        _ => null,
    };

    private static Template? RdsTemplate(JsonElement detail)
    {
        var categories = detail.TryGetProperty("EventCategories", out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()).ToArray() : [];
        if (categories.Contains("failover", StringComparer.OrdinalIgnoreCase))
        {
            return Warning("rds-failover", "The database reported a failover", "Review the RDS event and confirm connections have recovered. Avoid configuration changes while recovery is still in progress.");
        }

        if (categories.Contains("low storage", StringComparer.OrdinalIgnoreCase))
        {
            return new("rds-storage", "The database is low on storage", "Review free storage and the storage autoscaling limit. Capacity changes require operator approval.") { Severity = OpsFindingSeverity.Critical };
        }

        return categories.Any(c => c is "maintenance" or "notification" or "configuration change")
            ? new("rds-maintenance", "The database reported maintenance or a configuration event", "Review the RDS event and maintenance window for scheduled work or required operator action.") { Severity = OpsFindingSeverity.Info }
            : null;
    }

    private static Template? AlarmTemplate(JsonElement detail)
    {
        var state = detail.TryGetProperty("state", out var value) ? Text(value, "value") : "";
        return state switch
        {
            "ALARM" => Warning("cloudwatch-alarm", "An infrastructure alarm is active", "Review the CloudWatch alarm's metric, threshold and affected resource. The alarm does not authorize an automatic configuration change."),
            "OK" => new("cloudwatch-alarm-recovered", "An infrastructure alarm recovered", "CloudWatch reports the alarm returned to OK. Review the preceding event if failures continue.") { Severity = OpsFindingSeverity.Info },
            "INSUFFICIENT_DATA" => Warning("cloudwatch-alarm-no-data", "An infrastructure alarm has insufficient data", "CloudWatch cannot evaluate this alarm. Check metric publication and alarm configuration."),
            _ => null,
        };
    }

    private static string Resource(JsonElement root, JsonElement detail)
    {
        var group = Text(detail, "group");
        if (group.StartsWith("service:", StringComparison.Ordinal) && Text(detail, "clusterArn") is { Length: > 0 } cluster)
        {
            return $"{cluster}/{group}";
        }

        return root.TryGetProperty("resources", out var resources) && resources.ValueKind == JsonValueKind.Array
            ? resources.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()).FirstOrDefault() ?? Text(root, "source")
            : Text(root, "source");
    }

    private static int? ExitCode(JsonElement container) => container.ValueKind == JsonValueKind.Object && container.TryGetProperty("exitCode", out var code) && code.ValueKind == JsonValueKind.Number && code.TryGetInt32(out var value) ? value : null;

    private static bool IsFailure(string state) => state is "FAILED" or "EXPIRED" or "ABORTED";

    private static string Text(JsonElement element, string name) => element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    private static Template Warning(string rule, string title, string explanation) => new(rule, title, explanation);

    private sealed record Template(string Rule, string Title, string Explanation)
    {
        public OpsFindingSeverity Severity { get; init; } = OpsFindingSeverity.Warning;
    }
}

# ECS deployment alarm admission

Server alarm admission applies only to installations that configure an `AwsEcs` deploy target
(`ControlPlane:DeployTargets`). The CloudFormation Launch Stack configures no deploy target and
does not enable it: Launch Stack upgrades are a guided stack update, protected by the ECS service's
native deployment alarms (target 5xx, target latency, edge 5xx) and its deployment circuit breaker.

Installations that do configure an `AwsEcs` deploy target enable
`ControlPlane:AwsDeploymentAlarms:Enabled` and configure an alarm list for every managed deployment
target. The target identifier must match the configured deployment target exactly. Alarm names come from server configuration, never deployment requests.

```json
{
  "ControlPlane": {
    "AwsDeploymentAlarms": {
      "Enabled": true,
      "Targets": {
        "production": {
          "Regions": {
            "us-west-2": ["example-ops-target5xx", "example-ops-p97", "example-ops-composite"],
            "us-east-1": ["example-ops-edge5xx"]
          }
        }
      }
    }
  }
}
```

The equivalent first regional environment variable is
`ControlPlane__AwsDeploymentAlarms__Targets__production__Regions__us-west-2__0`.
Configure the edge alarm in its actual CloudWatch region, including `us-east-1` for CloudFront.
The task role needs `cloudwatch:DescribeAlarms` for the configured alarms, including composites.

Planning, operation creation, submission and forward checkpoint execution require every configured
alarm to be present and exactly `OK`. The server reads current state again at execution; an earlier
healthy plan does not authorize a later unhealthy deployment. Missing target configuration, empty or
invalid alarm lists, duplicate names, incomplete reads, provider errors and read timeouts refuse the
upgrade. `ALARM` and `INSUFFICIENT_DATA` both refuse it. Each admission attempt has a five-second
deadline across all regions. A target supports at most eight regions and one hundred alarms per
region. Configuration is captured when the admission service starts; restart after changing it.

A refusal never ends the deployment. A refused create or submit leaves the operation planned with
the reason in its error message; submit it again after the alarms recover. A refusal during a later
checkpoint step is recorded on the operation and logged, and the reconciler retries the step on its
next pass. Preparation admission is decided before the migration marker is written, so a refusal or
interruption at that point never requires manual intervention. Rollback remains available while alarms are unhealthy. Alarm admission defaults to disabled;
an installation with a deploy target must explicitly enable it and pass the complete regional lists. This setting does not create alarms, configure SNS delivery or prove a live gate pass.

# Launch Stack operations heartbeat

The Launch Stack binds `Observability__Heartbeat__StackName` to its CloudFormation
stack name. Leaving the setting empty disables the heartbeat. An optional
`Observability__Heartbeat__Region` selects the CloudWatch region; otherwise the
AWS SDK discovers the region from the task environment.

The server publishes `Honua/Operations`, metric `Heartbeat`, dimension
`StackName=<stack name>`, value `1`. PostgreSQL arbitrates a 60-second service
lease across tasks and process restarts. Each task checks every five seconds;
only the lease winner sends the metric. Failed database acquisition sends no
metric. A failed CloudWatch request retains the lease until expiry so another
task cannot flood the metric during a partial failure. Each attempt has a
10-second deadline. Lambda background polling profiles do not start this worker.

The task role needs `cloudwatch:PutMetricData`, restricted by the
`cloudwatch:namespace` condition to `Honua/Operations`. The regional Launch Stack
alarm uses `Maximum < 1` for three consecutive 60-second periods and treats
missing data as breaching. The alarm sends directly to the notification email
through SNS, so its delivery does not depend on server availability.

MAIN performs live qualification from the laptop: verify metric samples with two
healthy tasks, roll both tasks, stop the service long enough for three missing
periods and confirm the SNS notification. Restore the service and confirm the
alarm recovers. Record the deployed image digest, stack, alarm history and email
receipt against the release gate. Build-host tests establish lease coordination;
they do not establish the live AWS alarm or email delivery.

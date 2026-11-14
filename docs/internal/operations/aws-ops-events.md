# AWS operations event ingestion

An ECS service can consume an EventBridge SQS queue using
`Observability__OpsEvents__QueueUrl`. Set `Observability__OpsEvents__Region` when
the AWS SDK's normal region discovery does not identify the queue's region.
Omitting the URL disables the consumer. Lambda does not run this background loop.
The task role needs `sqs:ReceiveMessage` and `sqs:DeleteMessage` on that queue;
a customer-managed encryption key also needs its normal decrypt grant.

Provision the queue, EventBridge rules and dead-letter queue through the stack
module. The consumer long-polls for 20 seconds, receives at most ten messages and
uses a 120-second visibility timeout. The stack redrives after five receives and
retains messages for fourteen days. Processing failures leave the message for
redelivery; poison envelopes eventually reach the configured DLQ. Monitor the DLQ.

Each valid EventBridge envelope is committed to PostgreSQL before its SQS receipt
is deleted. `honua.ops_external_events.event_id` is unique across tasks and
restarts. Redelivery preserves the original body and creates the same finding
identity. The database retains receipts for thirty days; findings read at most
the newest thousand events from fourteen days. This bounds the dashboard query;
it is not an audit export of all infrastructure events.

Supported templates cover failed/OOM/image-pull ECS tasks, deployment failure and
completion, failed Backup jobs and copies, RDS maintenance/failover/storage events,
failed Batch jobs, ACM expiration warnings, AWS Health events, cost anomalies and
CloudWatch alarm transitions. Three distinct task ARNs stopping with errors for one ECS service in
fifteen minutes produce a Critical crash-loop finding. Only the latest received
alarm event by event time is presented for an alarm resource. These templates do
not authorize remediation or spending. Unknown event types are retained without
creating a finding. Invalid envelopes remain unacknowledged.

`IOpsExternalEventConsumer` runs after persistence and before acknowledgment,
including on duplicate deliveries. Handlers receive the original envelope with
its identity and UTC event time, even if a replay supplies a different body. They must deduplicate their own side effects and
fence the current service/release identity before acting. An old receiving task
is not evidence that its release completed. Handler failures retry through SQS.

For the isolated test stack, MAIN can run `scripts/ops/check-event-ingestion.py`
with its queue and DLQ URLs and `--execute`. It sends one failing Batch envelope
twice and one poison envelope. Expected results: one durable event and one finding
with the printed event id, no duplicate finding after restarting a server task,
and the poison body in the DLQ after five visibility periods. The script checks
the DLQ without deleting its message. MAIN also verifies the finding through the
normal Ops interface and records the result before the release freeze. No test
stack is created by this script.

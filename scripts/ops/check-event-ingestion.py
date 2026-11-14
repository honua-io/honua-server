#!/usr/bin/env python3
"""Run the queue redelivery fixture against an existing isolated test stack."""
import argparse
import datetime
import json
import subprocess
import time
import uuid


def aws(*arguments):
    result = subprocess.run(["aws", "sqs", *arguments, "--output", "json"],
                            check=True, capture_output=True, text=True)
    return json.loads(result.stdout)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--queue-url", required=True)
    parser.add_argument("--dlq-url", required=True)
    parser.add_argument("--execute", action="store_true", required=True)
    args = parser.parse_args()
    identity = str(uuid.uuid4())
    envelope = json.dumps({"version": "0", "id": identity, "source": "aws.batch",
                           "detail-type": "Batch Job State Change",
                           "time": datetime.datetime.now(datetime.timezone.utc).isoformat(),
                           "resources": [f"ops-chaos:{identity}"],
                           "detail": {"jobId": identity, "status": "FAILED"}})
    for _ in range(2):
        aws("send-message", "--queue-url", args.queue_url, "--message-body", envelope)
    poison = f"ops-poison-{identity}"
    aws("send-message", "--queue-url", args.queue_url, "--message-body", poison)
    print(f"Verify exactly one PostgreSQL event and Ops finding for event id {identity}.", flush=True)
    print("Restart one test-stack server task and verify the finding remains unique.", flush=True)
    deadline = time.monotonic() + 900
    while time.monotonic() < deadline:
        response = aws("receive-message", "--queue-url", args.dlq_url,
                       "--wait-time-seconds", "20", "--max-number-of-messages", "10")
        if any(message.get("Body") == poison for message in response.get("Messages", [])):
            print("PASS: poison envelope reached the DLQ; its message was preserved.")
            return
        time.sleep(5)
    raise SystemExit("FAIL: poison envelope did not reach the DLQ within fifteen minutes.")


if __name__ == "__main__":
    main()

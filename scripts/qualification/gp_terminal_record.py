"""Compare a submitted terminal CAS with its PostgreSQL committed projection."""

import datetime


def normalize(value, key=""):
    if isinstance(value, dict):
        return {name: normalize(item, name) for name, item in value.items()}
    if isinstance(value, list):
        return [normalize(item) for item in value]
    if isinstance(value, str) and key.endswith("At"):
        stamp = datetime.datetime.fromisoformat(value.replace("Z", "+00:00"))
        if stamp.tzinfo is None:
            raise ValueError("terminal timestamp must include its timezone")
        return stamp.astimezone(datetime.timezone.utc).isoformat(timespec="microseconds")
    return value


def verify_committed_record(submitted, persisted):
    """Allow only the version increment and timestamps projected by the successful CAS."""
    submitted, persisted = normalize(submitted), normalize(persisted)
    if submitted["version"] + 1 != persisted["version"] or persisted.get("updatedAt") is None:
        raise ValueError("terminal CAS did not advance exactly one persisted version")
    submitted["version"] = persisted["version"]
    submitted["updatedAt"] = persisted["updatedAt"]
    for field in ("claimedAt", "lastHeartbeatAt", "completedAt"):
        if submitted.get(field) != persisted.get(field):
            if submitted.get(field) is None or persisted.get(field) != persisted["updatedAt"]:
                raise ValueError("terminal CAS changed a timestamp outside the committed projection")
            submitted[field] = persisted[field]
    if submitted != persisted:
        raise ValueError("terminal CAS changed fields outside the committed projection")

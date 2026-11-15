"""Process snapshots shared by shard deadline supervisors."""
import subprocess


def descendant_pids(parent):
    # The deadline may still be signalling the supervisor's process group.
    try:
        listing = subprocess.check_output(['ps', '-e', '-o', 'pid=,ppid='], text=True,
                                          start_new_session=True, timeout=0.2)
    except (OSError, subprocess.TimeoutExpired):
        # Preserve group escalation even when process discovery is unavailable.
        return {parent}
    pairs = [tuple(map(int, line.split())) for line in listing.splitlines()]
    descendants = {parent}
    while True:
        expanded = descendants | {pid for pid, ppid in pairs if ppid in descendants}
        if expanded == descendants:
            return descendants
        descendants = expanded

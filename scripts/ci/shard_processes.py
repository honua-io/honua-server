"""Process snapshots shared by shard deadline supervisors."""
import subprocess


def descendant_pids(parent):
    # The deadline may still be signalling the supervisor's process group.
    listing = subprocess.check_output(['ps', '-e', '-o', 'pid=,ppid='], text=True,
                                      start_new_session=True)
    pairs = [tuple(map(int, line.split())) for line in listing.splitlines()]
    descendants = {parent}
    while True:
        expanded = descendants | {pid for pid, ppid in pairs if ppid in descendants}
        if expanded == descendants:
            return descendants
        descendants = expanded

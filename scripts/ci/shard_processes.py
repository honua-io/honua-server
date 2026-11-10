"""Process snapshots shared by shard deadline supervisors."""
import subprocess


def descendant_pids(parent):
    listing = subprocess.check_output(['ps', '-e', '-o', 'pid=,ppid='], text=True)
    pairs = [tuple(map(int, line.split())) for line in listing.splitlines()]
    descendants = {parent}
    while True:
        expanded = descendants | {pid for pid, ppid in pairs if ppid in descendants}
        if expanded == descendants:
            return descendants
        descendants = expanded

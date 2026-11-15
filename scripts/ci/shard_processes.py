"""Process snapshots shared by shard deadline supervisors."""
from contextlib import suppress
import os
import signal
import subprocess


def descendant_pids(*parents):
    # The deadline may still be signalling the supervisor's process group.
    listing = subprocess.check_output(['ps', '-e', '-o', 'pid=,ppid='], text=True,
                                      start_new_session=True)
    pairs = [tuple(map(int, line.split())) for line in listing.splitlines()]
    descendants = set(parents)
    while True:
        expanded = descendants | {pid for pid, ppid in pairs if ppid in descendants}
        if expanded == descendants:
            return descendants
        descendants = expanded


def kill_descendants(*parents):
    """SIGKILL every process under the parents, including ones forked during cleanup.

    One snapshot misses a child forked after it, and once its parent dies the child
    is reparented out of reach. Stopped processes cannot fork, so stop the tree until
    a snapshot finds nothing new, then kill everything stopped.
    """
    stopped = set()
    while fresh := descendant_pids(*parents, *stopped) - stopped:
        for pid in fresh:
            with suppress(ProcessLookupError):
                os.kill(pid, signal.SIGSTOP)
        stopped |= fresh
    for pid in stopped:
        with suppress(ProcessLookupError):
            os.kill(pid, signal.SIGKILL)

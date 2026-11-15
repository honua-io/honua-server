"""Process snapshots shared by shard deadline supervisors."""
from contextlib import suppress
import os
import signal
import subprocess

# The kill passes run after TERM, inside the kill-after grace, where a loaded runner's
# listing can exceed the pre-TERM snapshot's 0.2 s bound and hide a detached child.
KILL_LISTING_DEADLINE = 1.0


def descendant_depths(seeds, deadline=0.2):
    """Each process under the seeds (pid to depth, included as given) with its depth."""
    # The deadline may still be signalling the supervisor's process group.
    try:
        listing = subprocess.check_output(['ps', '-e', '-o', 'pid=,ppid='], text=True,
                                          start_new_session=True, timeout=deadline)
    except (OSError, subprocess.TimeoutExpired):
        # Preserve group escalation even when process discovery is unavailable.
        return dict(seeds)
    pairs = [tuple(map(int, line.split())) for line in listing.splitlines()]
    depths = dict(seeds)
    while level := {pid: depths[ppid] + 1 for pid, ppid in pairs if ppid in depths and pid not in depths}:
        depths.update(level)
    return depths


def descendant_pids(*parents):
    return set(descendant_depths(dict.fromkeys(parents, 0)))


def unstopped(depths, stopped):
    return {pid: depth for pid, depth in depths.items() if pid not in stopped}


def kill_descendants(*parents):
    """SIGKILL every process under the parents, including ones forked during cleanup.

    One snapshot misses a child forked after it, and once its parent dies the child
    is reparented out of reach. Stopped processes cannot fork, so stop the tree until
    a listing finds nothing new, then kill it deepest first: a dying parent would
    otherwise SIGCONT an orphaned stopped group before its members are killed.
    """
    roots, stopped = dict.fromkeys(parents, 0), {}
    try:
        while fresh := unstopped(descendant_depths({**roots, **stopped}, KILL_LISTING_DEADLINE), stopped):
            for pid in fresh:
                with suppress(ProcessLookupError):
                    os.kill(pid, signal.SIGSTOP)
            stopped.update(fresh)
    finally:
        # Never leave a stopped process behind, even if a listing failed part way.
        for pid in sorted(stopped, key=stopped.get, reverse=True):
            with suppress(ProcessLookupError):
                os.kill(pid, signal.SIGKILL)

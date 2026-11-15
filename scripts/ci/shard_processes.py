"""Process snapshots shared by shard deadline supervisors."""
from contextlib import suppress
import os
import signal
import subprocess


def descendant_depths(seeds):
    """Each process under the seeds (pid to depth, included as given) with its depth."""
    # The deadline may still be signalling the supervisor's process group.
    listing = subprocess.check_output(['ps', '-e', '-o', 'pid=,ppid='], text=True,
                                      start_new_session=True)
    pairs = [tuple(map(int, line.split())) for line in listing.splitlines()]
    depths = dict(seeds)
    while level := {pid: depths[ppid] + 1 for pid, ppid in pairs if ppid in depths and pid not in depths}:
        depths.update(level)
    return depths


def descendant_pids(*parents):
    return set(descendant_depths(dict.fromkeys(parents, 0)))


def kill_descendants(*parents):
    """SIGKILL every process under the parents, including ones forked during cleanup.

    One snapshot misses a child forked after it, and once its parent dies the child
    is reparented out of reach. Stopped processes cannot fork, so stop the tree until
    a listing finds nothing new, then kill it deepest first: a dying parent would
    otherwise SIGCONT an orphaned stopped group before its members are killed.
    """
    roots, stopped = dict.fromkeys(parents, 0), {}
    try:
        while fresh := {pid: depth for pid, depth in descendant_depths({**roots, **stopped}).items()
                        if pid not in stopped}:
            for pid in fresh:
                with suppress(ProcessLookupError):
                    os.kill(pid, signal.SIGSTOP)
            stopped.update(fresh)
    finally:
        # Never leave a stopped process behind, even if a listing failed part way.
        for pid in sorted(stopped, key=stopped.get, reverse=True):
            with suppress(ProcessLookupError):
                os.kill(pid, signal.SIGKILL)

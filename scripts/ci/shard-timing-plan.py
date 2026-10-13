#!/usr/bin/env python3
"""Read-only shard scheduling and reviewed collection-group rebalance proposals.

Timing is a scheduling hint, never selection or validation evidence. Recommendations
need passing TRX files and an explicit, reviewed collection/fixture grouping map.
No command in this tool writes routing configuration or starts tests.
"""
from __future__ import annotations

import argparse
from collections import defaultdict
from datetime import datetime, timezone, timedelta
import hashlib
from functools import lru_cache
import importlib.util
import json
import math
from pathlib import Path
import statistics
import sys
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[2]
MONOLITH = 'tests/dotnet/Honua.Server.Tests/Honua.Server.Tests.csproj'


@lru_cache(maxsize=1)
def filter_parser():
    spec = importlib.util.spec_from_file_location('shard_coverage', Path(__file__).with_name('check-server-test-shard-coverage.py'))
    module = importlib.util.module_from_spec(spec)
    sys.modules[spec.name] = module
    spec.loader.exec_module(module)
    return module


def canonical_filter(text, parser):
    def normalize(node):
        if node[0] == 'clause':
            return tuple(str(item).strip() for item in node)
        children = []
        for child in node[1]:
            value = normalize(child)
            children.extend(value[1] if value[0] == node[0] else [value])
        return (node[0], tuple(sorted(children, key=repr)))
    return normalize(parser._FilterParser(text.strip()).parse())


def fingerprint(config):
    entries = [{key: shard.get(key) for key in ('shard_name', 'csproj', 'filter')}
               for shard in config['shards']]
    return hashlib.sha256(json.dumps(entries, sort_keys=True, separators=(',', ':')).encode()).hexdigest()


def observations(config, directory, *, now=None, max_age_days=30):
    """Deduplicate copies and reject incomplete, old or differently filtered runs."""
    parser = filter_parser()
    now = now or datetime.now(timezone.utc)
    known = {s['shard_name']: s for s in config['shards']}
    expected = {name: canonical_filter('((' + s['filter'] + ')&Tier!=Slow)&Tier!=Fast', parser)
                for name, s in known.items()}
    result, seen = defaultdict(list), set()
    if not directory.is_dir():
        return result
    for path in sorted(directory.rglob('*.timing.json')):
        try:
            record = json.loads(path.read_text())
            name = record['shard']
            started = datetime.fromisoformat(record['started_at'].replace('Z', '+00:00'))
            seconds = record['duration_seconds']
            if (name not in known or started.tzinfo is None
                    or not now - timedelta(days=max_age_days) <= started <= now
                    or record.get('status') != 'passed' or record.get('timed_out')
                    or record.get('exit_code', 0) != 0
                    or record.get('capacity_status', 'ok') not in ('ok', 'low_headroom')
                    or isinstance(seconds, bool) or not isinstance(seconds, (float, int))
                    or not math.isfinite(seconds) or seconds <= 0):
                continue
            if canonical_filter(record['filter'], parser) != expected[name]:
                continue
            project = known[name].get('csproj') or MONOLITH
            if record.get('project', project) != project:
                continue
            identity = (name, record['started_at'])
            if identity in seen:
                continue
            seen.add(identity)
            result[name].append({'seconds': seconds, 'started': started,
                                 'timing': str(path), 'trx': path.with_name(path.name.removesuffix('.timing.json') + '.trx')})
        except (ValueError, TypeError, KeyError, OSError, AttributeError, OverflowError):
            continue
    for name in result:
        result[name] = sorted(result[name], key=lambda x: x['started'])[-10:]
    return result


def order_names(names, config, samples):
    known = {s['shard_name'] for s in config['shards']}
    if len(names) != len(set(names)) or set(names) - known:
        raise ValueError('Selected shards must be unique canonical shard names')
    # Incomplete evidence cannot manufacture a duration for an unknown shard.
    if any(not samples.get(name) for name in names):
        return names
    positions = {name: i for i, name in enumerate(names)}
    return sorted(names, key=lambda n: (-statistics.median(x['seconds'] for x in samples[n]), positions[n]))


def duration(text):
    hours, minutes, seconds = text.split(':')
    value = int(hours) * 3600 + int(minutes) * 60 + float(seconds)
    if not math.isfinite(value) or value < 0:
        raise ValueError('Invalid test duration')
    return value


def class_times(path):
    """Use TRX method identity, never the customizable display name."""
    root = ET.parse(path).getroot()
    namespace = {'t': root.tag.split('}')[0].lstrip('{')}
    methods = {}
    for test in root.findall('.//t:TestDefinitions/t:UnitTest', namespace):
        method = test.find('t:TestMethod', namespace)
        if method is not None and method.get('className'):
            methods[test.get('id')] = method.get('className').split(',')[0].strip()
    totals = defaultdict(float)
    counters = root.find('.//t:ResultSummary/t:Counters', namespace)
    results = root.findall('.//t:Results/t:UnitTestResult', namespace)
    if (counters is None or int(counters.get('failed', '0')) or not results
            or any(int(counters.get(key, '-1')) != len(results) for key in ('total', 'executed', 'passed'))):
        raise ValueError('Missing or failing TRX')
    for result in results:
        # A skipped or unidentifiable result is incomplete class evidence.
        if result.get('outcome') != 'Passed' or result.get('testId') not in methods:
            raise ValueError('Incomplete TRX class identity or execution')
        totals[methods[result.get('testId')]] += duration(result.get('duration', ''))
    return dict(totals)


def recommend(config, samples, groups, *, minimum_runs=3):
    report = {'schema': 'honua.shard-balance/v1', 'advisory': True,
              'routingFingerprint': fingerprint(config), 'proposals': [], 'gaps': []}
    projects = defaultdict(list)
    for shard in config['shards']:
        projects[shard.get('csproj') or MONOLITH].append(shard['shard_name'])
    if groups is not None and (groups.get('schema') != 'honua.shard-balance-groups/v1'
                               or groups.get('routingFingerprint') != fingerprint(config)):
        raise ValueError('Grouping map must match the current routing fingerprint')
    candidates = []
    for project, shards in sorted(projects.items()):
        if len(shards) < 2:
            continue
        try:
            if any(len(samples.get(s, [])) < minimum_runs for s in shards):
                raise ValueError(f'Need {minimum_runs} distinct complete runs for every project shard')
            if groups is None or project not in groups.get('projects', {}):
                raise ValueError('Reviewed collection/fixture grouping map required')
            source, history = {}, defaultdict(list)
            for shard in shards:
                previous = None
                for sample in samples[shard]:
                    current = class_times(sample['trx'])
                    if previous is not None and set(previous) != set(current):
                        raise ValueError('Class inventory changed across retained runs')
                    previous = current
                    for name, seconds in current.items():
                        if name in source and source[name] != shard:
                            raise ValueError('Class crosses existing shard boundaries')
                        source[name] = shard
                        history[name].append(seconds)
            grouped = groups['projects'][project]
            flat = [c for g in grouped for c in g['classes']]
            if (not grouped or any(not g.get('name') or not g.get('classes') for g in grouped)
                    or len(flat) != len(set(flat)) or set(flat) != set(history)
                    or len({g['name'] for g in grouped}) != len(grouped)):
                raise ValueError('Groups must cover every observed class exactly once')
            weighted = [(g, sum(statistics.median(history[c]) for c in g['classes'])) for g in grouped]
            current_load = {s: 0.0 for s in shards}
            for c in history:
                current_load[source[c]] += statistics.median(history[c])
            loads, assignments = {s: 0.0 for s in shards}, []
            for group, seconds in sorted(weighted, key=lambda x: (-x[1], x[0]['name'])):
                destination = min(shards, key=lambda s: (loads[s], s))
                loads[destination] += seconds
                assignments.append({'group': group['name'], 'classes': sorted(group['classes']),
                                    'from': sorted({source[c] for c in group['classes']}), 'to': destination,
                                    'summedTestSeconds': round(seconds, 3)})
            observed = max(statistics.median(x['seconds'] for x in samples[s]) for s in shards)
            candidates.append((observed, {'project': project, 'shardCount': len(shards),
                'currentSummedTestSeconds': current_load, 'proposedSummedTestSeconds': loads,
                'assignments': assignments, 'improvesSummedBalance': max(loads.values()) < max(current_load.values()),
                'note': 'Summed test durations are a balancing heuristic, not predicted wall time. Review fixture groups, update canonical filters and run exact-ownership guards before applying.'}))
        except (ValueError, TypeError, KeyError, OSError, ET.ParseError) as error:
            report['gaps'].append({'project': project, 'reason': str(error)})
    report['proposals'] = [r for _, r in sorted(candidates, key=lambda x: (-x[0], x[1]['project']))[:2]]
    return report


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('operation', choices=['order', 'recommend'])
    parser.add_argument('--config', type=Path, default=ROOT / '.github/ci-shards.json')
    parser.add_argument('--timings-dir', type=Path, required=True)
    parser.add_argument('--groups-file', type=Path)
    parser.add_argument('--minimum-runs', type=int, default=3)
    args = parser.parse_args(argv)
    if args.minimum_runs < 3:
        parser.error('At least three independent runs are required')
    config = json.loads(args.config.read_text())
    samples = observations(config, args.timings_dir)
    if args.operation == 'order':
        names = [line.strip() for line in sys.stdin if line.strip()]
        print('\n'.join(order_names(names, config, samples)))
    else:
        groups = json.loads(args.groups_file.read_text()) if args.groups_file else None
        print(json.dumps(recommend(config, samples, groups, minimum_runs=args.minimum_runs), indent=2))
    return 0


if __name__ == '__main__':
    try:
        sys.exit(main())
    except (ValueError, OSError, KeyError) as error:
        print(str(error), file=sys.stderr)
        sys.exit(2)

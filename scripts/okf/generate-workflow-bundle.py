#!/usr/bin/env python3
"""Validate canonical workflow sources and deterministically derive client bundle."""
import argparse
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
BASE = ROOT / 'docs/okf/workflows'

def build():
    vocabulary = json.loads((BASE / 'vocabulary.v0.1.json').read_text())
    grammar = json.loads((BASE / 'grammar.v0.1.json').read_text())
    terms = {t['id'] for t in vocabulary['terms']}
    if len(terms) != len(vocabulary['terms']): raise ValueError('Duplicate term')
    clarifications = {c['id']: c for c in vocabulary['clarifications']}
    aliases = {}
    for term in vocabulary['terms']:
        for alias in term['aliases']:
            text = ' '.join(alias['text'].lower().split())
            clarification = alias['clarification']
            if alias['ambiguous'] and clarification not in clarifications:
                raise ValueError(f'Missing clarification: {text}')
            if clarification and term['id'] not in clarifications[clarification]['choices']:
                raise ValueError(f'Clarification excludes term: {text}')
            aliases.setdefault(text, []).append(dict(termId=term['id'], **alias))
    for phrase, meanings in aliases.items():
        if len({m['termId'] for m in meanings}) > 1 and (not all(m['ambiguous'] for m in meanings) or len({m['clarification'] for m in meanings}) != 1):
            raise ValueError(f'Undeclared alias collision: {phrase}')
    for clarification in clarifications.values():
        if len(clarification['choices']) < 2 or not set(clarification['choices']) <= terms:
            raise ValueError('Invalid clarification choices')
    operations = grammar['operations']
    if len({op['id'] for op in operations}) != len(operations):
        raise ValueError('Duplicate operation')
    capabilities = json.loads((ROOT / 'docs/gis/data/operation-capability.v1.json').read_text())
    known = set()
    def walk(value):
        if isinstance(value, dict):
            for k, v in value.items():
                if k in ('capability', 'capabilityKey') and isinstance(v, str): known.add(v)
                walk(v)
        elif isinstance(value, list):
            for v in value: walk(v)
    walk(capabilities)
    known.update(p.stem for p in (ROOT / "docs/okf/capabilities").glob("*.md"))
    for op in operations:
        for capability in [op['capability'], *op.get('capabilityBySourceKind', {}).values()]:
            if capability and capability not in known:
                raise ValueError(f'Unknown capability: {capability}')
        for field in ('inputs', 'requires', 'produces', 'effectByTarget', 'approval', 'secretReferenceFields', 'identity', 'observation', 'binding', 'concurrencyPreconditions'):
            if field not in op: raise ValueError(f'Missing operation field: {field}')
        if len(op['identity']) < 4: raise ValueError('Unscoped operation identity')
        if op['executor'] != 'unavailable':
            raise ValueError('Executors require separate runtime qualification')
    return dict(bundleVersion=grammar['bundleVersion'], vocabulary=vocabulary, grammar=grammar,
                aliasIndex=aliases,
                edges=[dict(operation=op['id'], requires=op['requires'], produces=op['produces']) for op in operations],
                pathways=[dict(operation=op['id'], requires=op['requires'], produces=op['produces'], capability=op['capability'], executor=op['executor']) for op in operations if op['consoleAllowed']])

def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--check', action='store_true')
    args = parser.parse_args()
    bundle = build()
    output = json.dumps(bundle, indent=2) + '\n'
    documentation = '---\ntype: reference\ntitle: Generated workflow grammar\n---\n\n# Generated workflow grammar\n\nDo not edit: generated from grammar.v0.1.json. All executors require qualification.\n\n' + '\n'.join('## ' + op['id'] + '\n\nRequires: ' + ', '.join(op['requires']) + '. Produces: ' + ', '.join(op['produces']) + '. Executor: ' + op['executor'] + '.\n' for op in bundle['grammar']['operations'])
    path = BASE / 'generated/bundle.v0.1.json'
    if args.check:
        if path.read_text() != output or (BASE / 'generated/grammar.v0.1.md').read_text() != documentation: raise SystemExit('Workflow bundle is stale; regenerate it')
    else:
        path.write_text(output)
        (BASE / 'generated/grammar.v0.1.md').write_text(documentation)

if __name__ == '__main__': main()

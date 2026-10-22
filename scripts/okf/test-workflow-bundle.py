"""Contract tests for generator failure modes, independent of the .NET runtime."""
import importlib.util
import json
import tempfile
import unittest
from pathlib import Path

spec = importlib.util.spec_from_file_location('generator', Path(__file__).with_name('generate-workflow-bundle.py'))
generator = importlib.util.module_from_spec(spec)
spec.loader.exec_module(generator)

class BundleTests(unittest.TestCase):
    def setUp(self):
        self.original = generator.BASE
        self.temp = tempfile.TemporaryDirectory()
        generator.BASE = Path(self.temp.name)
        for name in ('vocabulary.v0.1.json', 'grammar.v0.1.json'):
            (generator.BASE / name).write_text((self.original / name).read_text())

    def tearDown(self):
        generator.BASE = self.original
        self.temp.cleanup()

    def mutate(self, action):
        path = generator.BASE / 'vocabulary.v0.1.json'
        data = json.loads(path.read_text())
        action(data)
        path.write_text(json.dumps(data))

    def test_valid_bundle_derives_every_edge(self):
        bundle = generator.build()
        self.assertEqual(len(bundle['grammar']['operations']), len(bundle['edges']))
        self.assertNotIn('platform.install', [p['operation'] for p in bundle['pathways']])
        self.assertTrue(all(op['executor'] == 'unavailable' for op in bundle['grammar']['operations']))

    def test_undeclared_collision_refused(self):
        self.mutate(lambda v: v['terms'][1]['aliases'].append(v['terms'][0]['aliases'][0]))
        with self.assertRaisesRegex(ValueError, 'Undeclared alias collision'): generator.build()

    def test_missing_clarification_refused(self):
        def change(v):
            alias = v['terms'][0]['aliases'][0]
            alias.update(ambiguous=True, clarification='missing')
        self.mutate(change)
        with self.assertRaisesRegex(ValueError, 'Missing clarification'): generator.build()

    def test_unknown_choice_refused(self):
        self.mutate(lambda v: v['clarifications'][0]['choices'].append('does-not-exist'))
        with self.assertRaisesRegex(ValueError, 'Invalid clarification choices'): generator.build()

if __name__ == '__main__': unittest.main()

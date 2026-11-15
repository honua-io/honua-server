#!/usr/bin/env python3
"""Reject colliding shard ownership while retaining grouped project execution."""
import importlib.util
from pathlib import Path
import unittest

SPEC = importlib.util.spec_from_file_location('identities', Path(__file__).with_name('check-shard-identities.py'))
CHECKER = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(CHECKER)


class ShardIdentityTests(unittest.TestCase):
    def test_duplicate_names_fail_even_with_distinct_projects(self):
        shards = [dict(name='same', filter='A', csproj='a.csproj'),
                  dict(name='same', filter='B', csproj='b.csproj')]
        self.assertIn("duplicate shard name 'same'", '\n'.join(CHECKER.find_collisions(shards)))

    def test_duplicate_default_project_filters_fail_with_owners(self):
        errors = CHECKER.find_collisions([dict(name='first', filter='A'), dict(name='second', filter='A')])
        self.assertEqual(len(errors), 1)
        self.assertIn("'first' and 'second'", errors[0])
        self.assertIn('Honua.Server.Tests.csproj', errors[0])

    def test_grouped_project_overlap_fails(self):
        shards = [dict(name='group', filter='A', csprojs=['a.csproj', 'b.csproj']),
                  dict(name='single', filter='A', csproj='b.csproj')]
        self.assertEqual(len(CHECKER.find_collisions(shards)), 1)

    def test_default_and_explicit_project_are_same_identity(self):
        shards = [dict(name='implicit', filter='A'),
                  dict(name='explicit', filter='A', csproj=CHECKER.shard_projects({})[0])]
        self.assertEqual(len(CHECKER.find_collisions(shards)), 1)

    def test_distinct_assemblies_may_share_namespace_filter(self):
        shards = [dict(name='group', filter='A', csprojs=['a.csproj', 'b.csproj']),
                  dict(name='single', filter='A', csproj='c.csproj')]
        self.assertEqual(CHECKER.find_collisions(shards), [])

    def test_distinct_filters_in_same_project_are_valid(self):
        self.assertEqual(CHECKER.find_collisions([dict(name='one', filter='A'), dict(name='two', filter='B')]), [])


if __name__ == '__main__':
    unittest.main()

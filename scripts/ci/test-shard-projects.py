#!/usr/bin/env python3
import unittest
from shard_projects import shard_projects


class ShardProjectsTests(unittest.TestCase):
    def test_legacy_default_and_single_project(self):
        self.assertTrue(shard_projects({})[0].endswith('Honua.Server.Tests.csproj'))
        self.assertEqual(['a.csproj'], shard_projects({'csproj': 'a.csproj'}))
        self.assertEqual(['a.csproj'], shard_projects({'csproj': 'a.csproj', 'csprojs': None}))

    def test_group_requires_distinct_nonempty_projects_and_no_single_project(self):
        self.assertEqual(['a.csproj', 'b.csproj'], shard_projects({'csprojs': ['a.csproj', 'b.csproj']}))
        for value in ([], ['a.csproj', 'a.csproj'], 'a.csproj', [''], [None]):
            with self.subTest(value=value), self.assertRaises(ValueError):
                shard_projects({'csprojs': value})
        with self.assertRaises(ValueError):
            shard_projects({'csproj': 'a.csproj', 'csprojs': ['b.csproj']})


if __name__ == '__main__':
    unittest.main()

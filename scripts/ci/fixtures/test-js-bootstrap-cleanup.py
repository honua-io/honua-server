"""Termination during bootstrap must release partially started owned fixtures."""
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[3]
CHILD = r'''
import os,runpy,signal,sys,types
from pathlib import Path
root,phase,marker=map(str,sys.argv[1:])
def record(kind):
    with open(marker,'a') as output: output.write(kind+'\n')
def terminate(stage):
    if phase==stage: os.kill(os.getpid(),signal.SIGTERM)
class Database:
    def start(self): terminate('database')
    def stop(self): record('database')
    def create_isolated_schema(self,_): return 'owned-schema'
    def drop_schema(self,_): record('schema')
    def seed_test_catalog(self,**_): pass
    def seed_test_features(self,**_): pass
    def get_npgsql_connection_string(self,**_): return 'fixture'
class Server:
    DEFAULT_PORT=5555
    def __init__(self,**_): pass
    def start(self,**_): terminate('server')
    def stop(self): record('server')
shared=types.ModuleType('shared')
shared.__path__=[str(Path(root)/'tests/python/shared')]
sys.modules['shared']=shared
sys.modules['shared.postgis']=types.SimpleNamespace(PostGISFixture=Database)
sys.modules['shared.server']=types.SimpleNamespace(HonuaServer=Server)
runpy.run_path(str(Path(root)/'tests/python/shared/js_test_server.py'))['main']()
'''


class BootstrapCleanupTests(unittest.TestCase):
    def test_termination_during_startup_reaps_owned_fixtures(self):
        for phase, expected in (("database", ["database"]), ("server", ["server", "schema", "database"])):
            with self.subTest(phase=phase), tempfile.TemporaryDirectory() as directory:
                marker = Path(directory) / "cleanup"
                result = subprocess.run([sys.executable, "-c", CHILD, str(ROOT), phase, str(marker)],
                                        capture_output=True, text=True, timeout=5)
                self.assertEqual(result.returncode, 0, result.stderr)
                self.assertEqual(marker.read_text().splitlines(), expected)


if __name__ == "__main__":
    unittest.main()

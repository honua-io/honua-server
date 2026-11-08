import os
from pathlib import Path
import re
import shutil
import subprocess
import tempfile
import unittest


class NativeRuntimeGateTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.base = Path(self.directory.name)
        self.runtime = self.base / 'runtime'
        self.app = self.runtime / 'app'
        self.app.mkdir(parents=True)
        for name in re.findall(r'/[^\s()]+', subprocess.check_output(['ldd', '/bin/true'], text=True)):
            original = Path(name)
            if original.is_file():
                for source in (original, original.resolve()):
                    target = self.runtime / str(source).lstrip('/')
                    target.parent.mkdir(parents=True, exist_ok=True)
                    shutil.copy2(source, target)
        fonts = self.runtime / 'usr/share/fonts/truetype/dejavu'
        fonts.mkdir(parents=True)
        (fonts / 'DejaVuSans.ttf').touch()
        shutil.copy2('/bin/true', self.app / 'honua-healthcheck')
        source = self.base / 'reader.c'
        source.write_text('#include <stdio.h>\nint reader(void){return puts("reader");}\n')
        subprocess.run(['cc', '-shared', '-fPIC', str(source), '-o',
                        str(self.app / 'ParquetSharpNative.so')], check=True)
        shutil.copy2(self.app / 'ParquetSharpNative.so', self.app / 'libSkiaSharp.so')

    def verify(self):
        script = Path(__file__).resolve().parents[2] / 'docker/runtime/verify-native.sh'
        command = ['sh', str(script), str(self.runtime), '/app']
        if os.geteuid() != 0:
            command = ['unshare', '-Ur', *command]
        return subprocess.run(command, text=True, capture_output=True, timeout=15)

    def host_only_dependency(self, target):
        host = self.base / 'host'
        host.mkdir()
        source = host / 'dependency.c'
        source.write_text('int dependency(void){return 42;}\n')
        subprocess.run(['cc', '-shared', '-fPIC', str(source),
                        '-Wl,-soname,libhostonlyfixture.so', '-o',
                        str(host / 'libhostonlyfixture.so')], check=True)
        source.write_text('extern int dependency(void);int reader(void){return dependency();}\n')
        subprocess.run(['cc', '-shared', '-fPIC', str(source), '-L' + str(host),
                        '-Wl,-rpath,' + str(host), '-lhostonlyfixture', '-o', str(target)], check=True)

    def test_complete_root_passes(self):
        result = self.verify()
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertNotIn(str(self.runtime), result.stdout)

    def test_dependency_available_only_on_build_host_fails(self):
        self.host_only_dependency(self.app / 'ParquetSharpNative.so')
        result = self.verify()
        self.assertNotEqual(result.returncode, 0)
        self.assertIn('libhostonlyfixture.so', result.stderr)

    def test_undefined_native_function_is_rejected(self):
        source = self.base / 'unresolved.c'
        source.write_text('#include <stdio.h>\nextern int unavailable(void);int reader(void){puts("reader");return unavailable();}\n')
        subprocess.run(['cc', '-shared', '-fPIC', str(source), '-o',
                        str(self.app / 'ParquetSharpNative.so')], check=True)
        result = self.verify()
        self.assertNotEqual(result.returncode, 0)
        self.assertIn('undefined symbol', result.stdout + result.stderr)

    def test_alternate_native_variant_is_left_to_runtime_selection(self):
        shutil.copy2(self.app / 'ParquetSharpNative.so', self.app / 'librdkafka.so')
        self.host_only_dependency(self.app / 'centos8-librdkafka.so')
        result = self.verify()
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)

    def test_primary_client_native_must_resolve_its_dependencies(self):
        self.host_only_dependency(self.app / 'librdkafka.so')
        result = self.verify()
        self.assertNotEqual(result.returncode, 0)
        self.assertIn('libhostonlyfixture.so', result.stderr)

    def test_no_compatible_client_variant_is_rejected(self):
        self.host_only_dependency(self.app / 'centos8-librdkafka.so')
        self.assertNotEqual(self.verify().returncode, 0)

    def test_compatible_prefixed_client_variant_is_accepted(self):
        shutil.copy2(self.app / 'ParquetSharpNative.so', self.app / 'centos8-librdkafka.so')
        result = self.verify()
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)

    def test_managed_client_without_any_native_variant_is_rejected(self):
        (self.app / 'Confluent.Kafka.dll').touch()
        self.assertNotEqual(self.verify().returncode, 0)


if __name__ == '__main__':
    unittest.main()

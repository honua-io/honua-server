"""Third-party images in the customer install artifacts are pinned by digest and listed for scanning."""
import io
import tempfile
import unittest
from contextlib import redirect_stderr, redirect_stdout
from pathlib import Path

import shipped_images
from package_quickstart import CONSOLE_WEB_IMAGE

DIGEST = 'sha256:' + 'a' * 64


class ShippedImageTests(unittest.TestCase):
    def test_every_shipped_third_party_image_is_pinned_by_digest(self):
        found = shipped_images.shipped_images()
        self.assertEqual([], shipped_images.unpinned(found))

    def test_inventory_covers_the_database_console_and_grid_build(self):
        images = {image for _, image in shipped_images.shipped_images()}
        self.assertIn(CONSOLE_WEB_IMAGE, images)
        found = shipped_images.shipped_images()
        databases = {image for location, image in found
                     if location.startswith(('docs/get-started/compose.executor.yaml', 'docker/proj-grids/Dockerfile'))
                     and image.startswith('ghcr.io/')}
        quickstart_base = {image for location, image in found if location.startswith('docs/get-started/quickstart.md')
                           and not image.startswith('alpine:')}
        self.assertEqual(1, len(databases | quickstart_base), databases | quickstart_base)
        self.assertLessEqual(databases, images)
        self.assertTrue(
            any(location.startswith('docker/proj-grids/Dockerfile:') and image.startswith('alpine:')
                for location, image in found),
            found)

    def test_lock_variables_and_locally_built_images_are_not_listed(self):
        text = (f'services:\n  server:\n    image: ${{HONUA_IMAGE:?Set}}\n'
                f'  db:\n    image: honua-local:1\n    build:\n      args:\n'
                f'        BASE_IMAGE: base/db:1@{DIGEST}\n  web:\n    image: "web/nginx:1@{DIGEST}"\n')
        found = list(shipped_images.references('compose.yaml', text))
        self.assertEqual([('compose.yaml:8', f'base/db:1@{DIGEST}'), ('compose.yaml:10', f'web/nginx:1@{DIGEST}')],
                         found)

    def test_tags_and_malformed_digests_are_unpinned(self):
        found = [('a', 'postgres:17'), ('b', 'postgres@sha256:abc'), ('c', f'postgres:17@{DIGEST.upper()}'),
                 ('d', f'docker.io/postgres:17@{DIGEST}')]
        self.assertEqual(['a', 'b', 'c'], [location for location, _ in shipped_images.unpinned(found)])

    def test_dockerfile_base_and_stage_references_are_read(self):
        text = (f'ARG BASE_IMAGE=base/db:1@{DIGEST}\nFROM --platform=$BUILDPLATFORM debian:13@{DIGEST} AS grids\n'
                f'FROM grids AS check\nFROM scratch\nFROM ${{BASE_IMAGE}}\n')
        self.assertEqual([f'base/db:1@{DIGEST}', f'debian:13@{DIGEST}'],
                         [image for _, image in shipped_images.references('Dockerfile', text)])

    def test_cli_refuses_an_unpinned_image_and_lists_nothing(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            for relative_path in shipped_images.SHIPPED_FILES:
                (root / relative_path).parent.mkdir(parents=True, exist_ok=True)
                (root / relative_path).write_text('')
            (root / 'docs/get-started/compose.executor.yaml').write_text('    image: pgrouting/pgrouting:17\n')
            stdout, stderr = io.StringIO(), io.StringIO()
            original = shipped_images.ROOT
            shipped_images.ROOT = root
            try:
                with redirect_stdout(stdout), redirect_stderr(stderr):
                    code = shipped_images.main(['--list'])
            finally:
                shipped_images.ROOT = original
            self.assertEqual(1, code)
            self.assertEqual('', stdout.getvalue())
            self.assertIn('compose.executor.yaml:1 pulls pgrouting/pgrouting:17 by tag', stderr.getvalue())


if __name__ == '__main__':
    unittest.main()

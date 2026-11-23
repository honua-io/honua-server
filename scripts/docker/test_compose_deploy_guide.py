"""The Compose deploy guide's commands must fit the quickstart project they run in."""
import re
import unittest

from compose_quickstart import COMPOSE_FILES
from quickstart_testing import ROOT


GUIDE = ROOT / 'docs/guides/deploy/docker-compose.md'
IMAGE_OVERRIDE = 'compose.executor.image.yaml'
INSTALL_FILES = (*COMPOSE_FILES, IMAGE_OVERRIDE, 'images.json', 'edge.yaml', 'secrets.tar.gz')


def fenced_blocks(text, language):
    return re.findall(rf'```{language}\n(.*?)```', text, re.DOTALL)


def section(text, heading):
    return text.split(f'\n## {heading}\n', 1)[1].split('\n## ', 1)[0]


class ComposeDeployGuideTests(unittest.TestCase):
    def setUp(self):
        self.guide = GUIDE.read_text()

    def test_quickstart_shorthand_keeps_the_saved_compose_file_list(self):
        # An explicit -f makes Compose ignore COMPOSE_FILE, dropping the executor, Console and image pin.
        definitions = [line for line in self.guide.splitlines()
                       if re.match(r'\s*dc\(\) \{ docker compose', line) and 'docker-compose.production.yml' not in line]
        self.assertTrue(definitions)
        for line in definitions:
            self.assertNotRegex(line, r'\s-f\s', line)

    def test_backup_copies_every_install_file_a_restore_needs(self):
        backup = section(self.guide, 'Backup and recovery')
        bash = next(block for block in fenced_blocks(backup, 'bash') if 'Backup="$Install/backup-' in block)
        powershell = next(block for block in fenced_blocks(backup, 'powershell') if "$Backup = Join-Path $Install" in block)
        for block in (bash, powershell):
            for name in INSTALL_FILES:
                self.assertIn(name, block, f'the backup must keep {name}')

    def test_diagnostics_and_upgrade_name_the_image_the_quickstart_runs(self):
        self.assertNotIn("grep '^HONUA_IMAGE=' .env", self.guide,
                         'the quickstart runs the image pinned in the executor override, not .env HONUA_IMAGE')
        self.assertIn(IMAGE_OVERRIDE, section(self.guide, 'Upgrade & Rollback'))


if __name__ == '__main__':
    unittest.main()

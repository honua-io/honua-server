"""The private prior retains restore coverage and visible advisory warnings."""
from pathlib import Path
import tempfile
import unittest
from prepare_lifecycle_prior import cap_schema, apply_advisory_policy


class LifecyclePriorTests(unittest.TestCase):
    def test_preserves_old_scripts_and_other_content(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            migrations = root / 'src/Honua.Server/Migrations'
            migrations.mkdir(parents=True)
            for name in ('001_Init.sql', '140_Expand.sql', '141_Contract.sql',
                         '142_Financial.sql', 'notes.txt'):
                (migrations / name).write_text(name)
            entrypoint = root / 'secret-entrypoint.c'
            entrypoint.write_text('launcher source')
            cap_schema(root)
            self.assertEqual(['001_Init.sql', '140_Expand.sql', 'notes.txt'],
                             sorted(path.name for path in migrations.iterdir()))
            self.assertEqual('140_Expand.sql', (migrations / '140_Expand.sql').read_text())
            self.assertEqual('launcher source', entrypoint.read_text())

    def test_advisories_remain_visible_without_failing_the_fixture_build(self):
        import xml.etree.ElementTree as ET
        with tempfile.TemporaryDirectory() as directory:
            props = Path(directory) / 'Directory.Build.props'
            props.write_text('<Project><PropertyGroup>'
                             '<TreatWarningsAsErrors>true</TreatWarningsAsErrors>'
                             '<WarningsNotAsErrors>CS1234</WarningsNotAsErrors>'
                             '</PropertyGroup></Project>')
            project = Path(directory) / 'src/Honua.Core/Honua.Core.csproj'
            project.parent.mkdir(parents=True)
            project.write_text('<Project><PropertyGroup>'
                               '<WarningsNotAsErrors>CA2009;IDE0073</WarningsNotAsErrors>'
                               '</PropertyGroup></Project>')
            apply_advisory_policy(directory)
            self.assertEqual('$(WarningsNotAsErrors);CA2009;IDE0073',
                             ET.parse(project).getroot().findtext('.//WarningsNotAsErrors'))
            root = ET.parse(props).getroot()
            self.assertEqual('true', root.findtext('.//TreatWarningsAsErrors'))
            warnings = root.findall('.//WarningsNotAsErrors')
            self.assertEqual('CS1234', warnings[0].text)
            self.assertEqual('$(WarningsNotAsErrors);NU1901;NU1902;NU1903;NU1904',
                             warnings[-1].text)
            self.assertIsNone(root.find('.//NoWarn'))
            self.assertIsNone(root.find('.//NuGetAudit'))

    def test_rejects_wrong_source_before_modifying_files(self):
        with tempfile.TemporaryDirectory() as directory:
            migrations = Path(directory) / 'src/Honua.Server/Migrations'
            migrations.mkdir(parents=True)
            script = migrations / '142_Financial.sql'
            script.write_text('unchanged')
            with self.assertRaisesRegex(ValueError, 'schema 140'):
                cap_schema(directory)
            self.assertEqual('unchanged', script.read_text())


if __name__ == '__main__':
    unittest.main()

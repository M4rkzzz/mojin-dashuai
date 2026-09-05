import hashlib
import importlib.util
import json
from pathlib import Path
import sys
import tempfile
import tomllib
import unittest
import zipfile

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / 'tools'))
import pack_distribution as pack


class DistributionTests(unittest.TestCase):
    def setUp(self):
        self.config = json.loads((ROOT / 'packs/distributions.json').read_text(encoding='utf-8'))

    def test_windows_paths_reject_traversal_and_aliases(self):
        for value in ['../x', '/x', 'C:/x', 'a\\b', 'a//b', 'a/./b', 'mods/NUL.jar', 'a./b', 'a/b ', 'a:b', 'a\x00b']:
            with self.subTest(value=value), self.assertRaises(ValueError):
                pack.safe_path(value)
        self.assertEqual(pack.safe_path('mods/1.7.10/+Library.jar'), 'mods/1.7.10/+Library.jar')

    def test_downloads_cannot_contain_credentials(self):
        for url in ['http://example.org/x', 'https://user:password@example.org/x', 'https://example.org/x?token=secret', 'https://example.org/a b', 'file:///x']:
            with self.subTest(url=url), self.assertRaises(ValueError):
                pack.public_url(url)

    def test_runtime_rules_and_no_cleanroom_mrpack_disguise(self):
        for key, spec in self.config['instances'].items():
            pack.validate_spec(key, spec)
        spec = dict(self.config['instances']['mb'])
        for major in (8, 17, 21, 26):
            spec['javaMajor'] = major
            with self.assertRaises(ValueError):
                pack.validate_spec('mb', spec)
        with self.assertRaises(ValueError):
            pack.mrpack_index(self.config['instances']['mb'], [])

    def test_overrides_keep_pack_profiles_and_omit_private_state(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            names = ['config/lostcities/profiles/deceasedcraft.json', 'config/machine/token_totem.json',
                     'config/cache/logs/latest.log', 'config/ItemFavorites/player.dat',
                     'config/modularmachinery/machinery/induction_electrolyzer.json', 'saves/world/level.dat']
            for name in names:
                p = root / name
                p.parent.mkdir(parents=True, exist_ok=True)
                p.write_bytes(b'{}')
            entries, _ = pack.override_files(root, self.config['instances']['mb'])
            self.assertIn(names[0], entries)
            self.assertIn(names[1], entries)
            for name in names[2:]:
                self.assertNotIn(name, entries)
            self.assertEqual(entries['options.txt'], b'lang:zh_cn\nfullscreen:false\n')

    def test_nonempty_credential_blocks_export_without_echoing_it(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            (root / 'config').mkdir()
            (root / 'config/auth.json').write_text('{"accessToken":"SyntheticSecret123456789012345"}')
            with self.assertRaises(ValueError) as error:
                pack.override_files(root, self.config['instances']['m3e'])
            self.assertNotIn('SyntheticSecret', str(error.exception))

    def test_modrinth_required_hashes_and_anonymous_mirrors(self):
        row = {'path': 'mods/test.jar', 'size': 3, **pack.digest(b'jar'),
               'sources': ['https://author.example/test.jar', 'https://launcher.boshan.uk/objects/hash.jar']}
        index = pack.mrpack_index(self.config['instances']['m3e'], [row])
        self.assertEqual(index['dependencies'], {'minecraft': '1.7.10', 'forge': '10.13.4.1614'})
        self.assertEqual(len(index['files'][0]['hashes']['sha512']), 128)
        self.assertEqual(index['files'][0]['downloads'], row['sources'])
        self.assertEqual(index['files'][0]['env']['client'], 'required')

    def test_packwiz_index_hashes_preserve_settings_and_pin_mods(self):
        row = {'path': 'mods/test.jar', 'size': 3, **pack.digest(b'jar'), 'sources': ['https://example.org/test.jar']}
        files = pack.packwiz_files(self.config['instances']['mb'], [row], {'options.txt': b'lang:zh_cn', 'config/machine.json': b'{}'})
        main = tomllib.loads(files['pack.toml'].decode())
        self.assertEqual(main['index']['hash'], hashlib.sha256(files['index.toml']).hexdigest())
        self.assertNotIn('forge', main['versions'])
        index = tomllib.loads(files['index.toml'].decode())
        for item in index['files']:
            self.assertEqual(item['hash'], hashlib.sha256(files[item['file']]).hexdigest())
        self.assertTrue(next(i for i in index['files'] if i['file'] == 'options.txt')['preserve'])
        mod = tomllib.loads(files['mods/test.jar.pw.toml'].decode())
        self.assertNotIn('update', mod)
        self.assertEqual(mod['download']['hash'], row['sha256'])

    def test_deterministic_zip_and_no_duplicate_windows_paths(self):
        with tempfile.TemporaryDirectory() as tmp:
            a, b = Path(tmp) / 'a.zip', Path(tmp) / 'b.zip'
            pack.write_zip(a, {'modrinth.index.json': b'{}', 'overrides/config/a.cfg': b'test'})
            pack.write_zip(b, {'overrides/config/a.cfg': b'test', 'modrinth.index.json': b'{}'})
            self.assertEqual(a.read_bytes(), b.read_bytes())
            with self.assertRaises(ValueError):
                pack.write_zip(b, {'mods/A.jar': b'1', 'mods/a.jar': b'2'})

    def test_routes_use_equal_names_and_keep_domains(self):
        for spec in self.config['instances'].values():
            self.assertEqual([r['name'] for r in spec['routes']], ['河北阿里云', '宿迁三网'])
            data = pack.servers_dat(spec['routes'])
            for route in spec['routes']:
                self.assertIn(route['host'].encode(), data)

    def test_skin_config_uses_own_api_then_official_and_keeps_tls_validation(self):
        for instance in self.config['instances']:
            config = json.loads(pack.skin_config(instance))
            self.assertEqual([x['type'] for x in config['loadlist']], ['CustomSkinAPI', 'MojangAPI'])
            self.assertEqual(config['loadlist'][0]['root'], 'https://launcher.boshan.uk/v1/skins/csl/')
            self.assertFalse(config['forceIgnoreHttpsCertificate'])
        self.assertEqual(json.loads(pack.skin_config('m3e'))['version'], '14.17')


if __name__ == '__main__':
    unittest.main()

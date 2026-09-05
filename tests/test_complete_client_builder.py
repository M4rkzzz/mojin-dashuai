import copy
import hashlib
import importlib.util
import io
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest import mock
import zipfile

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / 'tools'))
SPEC = importlib.util.spec_from_file_location('complete_builder', ROOT / 'tools/build-complete-client.py')
builder = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(builder)


class CompleteClientBuilderTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.cache = self.root / 'cache'
        self.cache.mkdir()
        self.manifest_path = self.root / 'input.json'
        self.url = 'https://downloads.example.test/objects/sha256'
        self.manifest = {
            'instance': 'fixture', 'version': 'baseline', 'sequence': 1, 'minecraft': '1.20.1',
            'loader': 'forge', 'loaderVersion': '47.4.0', 'launchVersion': '1.20.1-forge',
            'compatibility': 'fixture-forge', 'memoryMiB': 4096,
            'files': [self.object('mods/example.jar', b'pinned jar'),
                      self.object('config/中文.json', b'{"publicDefault":true}', 'seed'),
                      self.object('assets/objects/aa/example', b'asset')],
            'runtime': {'id': 'temurin-fixture', 'major': 17, 'version': '17-fixture',
                        'platform': 'windows-x64', 'javaPath': 'jdk/bin/java.exe', 'expandedSize': 4,
                        'archive': self.object('runtime.zip', self.runtime_zip())},
            'validationEvidence': ['old-acceptance.json'],
            'bundles': [{'archive': {'path': 'overrides.zip'}, 'prefix': 'overrides/'}]}
        self.save()

    @staticmethod
    def runtime_zip():
        data = io.BytesIO()
        with zipfile.ZipFile(data, 'w') as archive:
            archive.writestr('jdk/bin/java.exe', b'java')
        return data.getvalue()

    def object(self, path, data, policy='managed', suffix=''):
        digest = hashlib.sha256(data).hexdigest()
        (self.cache / (digest + suffix)).write_bytes(data)
        return {'path': path, 'size': len(data), 'sha256': digest, 'sources': [self.url + '/' + digest],
                'policy': policy, 'distributionBasis': 'Synthetic fixture'}

    def save(self):
        self.manifest_path.write_text(json.dumps(self.manifest, ensure_ascii=False), encoding='utf-8')

    def build(self, name='output', **kwargs):
        self.save()
        return builder.build(self.manifest_path, self.root / name, 'candidate.2', 2,
                             object_roots=[self.cache], public_base=self.url, **kwargs)

    def fail(self, expected, **kwargs):
        with self.assertRaisesRegex(builder.BuildError, expected):
            self.build(**kwargs)
        output = self.root / 'output'
        self.assertFalse((output / 'complete-client.zip').exists())
        self.assertFalse((output / 'manifest.candidate.json').exists())
        self.assertEqual([p.name for p in output.iterdir()], ['report.json'])
        report = json.loads((output / 'report.json').read_text(encoding='utf-8'))
        self.assertFalse(report['candidate'])
        return report

    def test_complete_roundtrip_preserves_all_file_runtime_and_update_records(self):
        original = self.manifest_path.read_bytes()
        report = self.build()
        output = self.root / 'output'
        self.assertEqual(self.manifest_path.read_bytes(), original)
        self.assertTrue(report['candidate'])
        self.assertFalse(report['signed'])
        self.assertFalse(report['uploaded'])
        self.assertEqual(report['verifiedZipEntries'], 4)
        candidate = builder.read_json(output / 'manifest.candidate.json')
        self.assertEqual(candidate['files'], self.manifest['files'])
        self.assertEqual(candidate['runtime'], self.manifest['runtime'])
        self.assertEqual(candidate['validationEvidence'], [])
        self.assertEqual((candidate['version'], candidate['sequence']), ('candidate.2', 2))
        bundle, = candidate['bundles']
        self.assertTrue(bundle['complete'])
        self.assertEqual(bundle['prefix'], '')
        self.assertEqual(bundle, builder.read_json(output / 'bundle.json'))
        self.assertEqual(bundle['archive']['sha256'], builder.sha256(output / 'complete-client.zip'))
        self.assertEqual(bundle['archive']['sources'], [self.url + '/' + bundle['archive']['sha256']])
        with zipfile.ZipFile(output / 'complete-client.zip') as archive:
            self.assertEqual(set(archive.namelist()), {*[r['path'] for r in self.manifest['files']], builder.RUNTIME_ENTRY})
            for row in self.manifest['files']:
                self.assertEqual(hashlib.sha256(archive.read(row['path'])).hexdigest(), row['sha256'])
            self.assertEqual(hashlib.sha256(archive.read(builder.RUNTIME_ENTRY)).hexdigest(), self.manifest['runtime']['archive']['sha256'])

    def test_bundle_bytes_are_deterministic_with_same_inputs(self):
        self.build('a')
        self.manifest['files'].reverse()
        self.build('b')
        self.assertEqual((self.root / 'a/complete-client.zip').read_bytes(), (self.root / 'b/complete-client.zip').read_bytes())

    def test_output_cannot_overwrite_input_or_existing_directory(self):
        before = self.manifest_path.read_bytes()
        for output in (self.manifest_path, self.cache):
            with self.subTest(output=output), self.assertRaisesRegex(builder.BuildError, 'new directory'):
                builder.build(self.manifest_path, output, 'candidate.2', 2, public_base=self.url)
        self.assertEqual(before, self.manifest_path.read_bytes())

    def test_explicit_new_version_and_increasing_sequence_are_required(self):
        for i, (version, sequence) in enumerate([('baseline', 2), ('', 2), ('new', 1), ('new', 0), ('new', True)]):
            with self.subTest(version=version, sequence=sequence), self.assertRaises(builder.BuildError):
                builder.build(self.manifest_path, self.root / str(i), version, sequence, public_base=self.url)

    def test_missing_mod_fails_without_smaller_zip(self):
        (self.cache / self.manifest['files'][0]['sha256']).unlink()
        self.fail('Required object unavailable')

    def test_initial_release_requires_explicit_unpublished_sequence_one(self):
        self.manifest['bundles'] = []
        self.manifest['validationEvidence'] = []
        self.save()
        report = builder.build(self.manifest_path, self.root / 'initial', 'baseline', 1,
                               object_roots=[self.cache], public_base=self.url, initial_release=True)
        self.assertTrue(report['initialRelease'])
        self.assertEqual(report['sequence'], 1)
        for i, change in enumerate([{'sequence': 2}, {'bundles': [{'complete': True}]},
                                    {'validationEvidence': ['previous-acceptance.json']} ]):
            candidate = {**self.manifest, **change}
            with self.subTest(change=change), self.assertRaises(builder.BuildError):
                builder.validate_manifest(candidate, 'baseline', 1, initial_release=True)
        for version, sequence in [('different', 1), ('baseline', 2), ('baseline', True)]:
            with self.subTest(version=version, sequence=sequence), self.assertRaises(builder.BuildError):
                builder.validate_manifest(self.manifest, version, sequence, initial_release=True)

    def test_missing_runtime_fails_without_candidate(self):
        (self.cache / self.manifest['runtime']['archive']['sha256']).unlink()
        self.fail('Required object unavailable')

    def test_changed_same_size_mod_fails_sha256(self):
        row = self.manifest['files'][0]
        (self.cache / row['sha256']).write_bytes(b'x' * row['size'])
        self.fail('SHA256/size mismatch')

    def test_changed_runtime_fails_sha256(self):
        row = self.manifest['runtime']['archive']
        (self.cache / row['sha256']).write_bytes(b'x' * row['size'])
        self.fail('SHA256/size mismatch')

    def test_duplicate_case_insensitive_manifest_paths_fail(self):
        self.manifest['files'].append({**self.manifest['files'][0], 'path': 'MODS/EXAMPLE.jar'})
        self.fail('Duplicate manifest destination')

    def test_path_traversal_and_windows_aliases_fail_before_reading_objects(self):
        invalid = ['../escape', '/absolute', 'C:/escape', 'mods\\escape', 'a//b', 'a/./b', 'a/../b',
                   'NUL.jar', 'CON .json', 'a/foo.', 'a/b ', 'a:b', 'a\x00b', '__runtime/runtime.zip', '__RUNTIME/other']
        for i, path in enumerate(invalid):
            self.manifest['files'][0]['path'] = path
            with self.subTest(path=path), self.assertRaises(builder.BuildError):
                self.build(str(i))

    def test_file_directory_collision_fails(self):
        self.manifest['files'].append(self.object('mods', b'bad directory'))
        self.fail('file/directory collision')

    def test_player_files_are_rejected_even_when_pinned(self):
        for i, path in enumerate(['saves/world/level.dat', 'screenshots/me.png',
                                  'config/accounts/data.json', '.hub/state.json', 'config/ItemFavorites/player.dat',
                                  'config/usercache.json', 'accounts.json', 'credentials.json', 'logs/latest.log']):
            self.manifest['files'][0]['path'] = path
            with self.subTest(path=path), self.assertRaisesRegex(builder.BuildError, 'private state'):
                self.build(str(i))

    def test_pinned_seed_defaults_are_allowed_and_never_read_from_player_directory(self):
        self.manifest['files'].append(self.object('options.txt', b'lang:zh_cn\nfullscreen:false\n', 'seed'))
        self.assertTrue(self.build()['candidate'])

    def test_secret_content_blocks_without_echoing_value(self):
        secret = 'SyntheticSecret123456789012345'
        self.manifest['files'].append(self.object('config/auth.json', ('{"accessToken":"' + secret + '"}').encode('utf-8')))
        self.fail('Possible credential')
        self.assertNotIn(secret, (self.root / 'output/report.json').read_text(encoding='utf-8'))

    def test_denied_redistribution_is_blocked_without_silent_exclusion(self):
        report = self.fail('Redistribution policy blocks', denied=['mods/*.jar'])
        self.assertEqual(report['blockers'][0]['path'], 'mods/example.jar')
        self.assertEqual(report['verifiedFiles'], 0)

    def test_hash_policy_blocks_renamed_nonredistributable_file(self):
        policy = self.root / 'policy.json'
        policy.write_bytes(builder.json_bytes({'schema': 1, 'blocked': [
            {'sha256': self.manifest['files'][0]['sha256'], 'reason': 'Synthetic license prohibition'}]}))
        self.fail('Redistribution policy blocks', redistribution_policy=policy)

    def test_official_exception_is_explicit_omitted_and_reported_with_bytes(self):
        row = self.manifest['files'][0]
        row.update(officialOnly=True, sources=['https://mediafilez.forgecdn.net/files/123/456/example.jar'])
        (self.cache / row['sha256']).unlink()  # Official exception must not require a local rehost copy.
        report = self.build(denied=['mods/*.jar'])
        self.assertFalse(report['allManifestFilesInZip'])
        self.assertEqual(report['officialOnlyBytes'], row['size'])
        self.assertEqual(report['officialOnlyFiles'][0]['sha256'], row['sha256'])
        self.assertFalse(report['officialOnlyDownloadsVerified'])
        self.assertEqual(report['declaredFiles'], 3)
        self.assertEqual(report['bundledFiles'], 2)
        self.assertEqual(report['verifiedZipEntries'], 3)
        candidate = builder.read_json(self.root / 'output/manifest.candidate.json')
        self.assertEqual(candidate['files'], self.manifest['files'])
        with zipfile.ZipFile(self.root / 'output/complete-client.zip') as archive:
            self.assertNotIn(row['path'], archive.namelist())

    def test_official_exception_cannot_have_ambiguous_or_untrusted_download(self):
        row = self.manifest['files'][0]
        for i, sources in enumerate([['https://mirror.example/mod.jar'], ['https://cdn.modrinth.com/a', 'https://cdn.modrinth.com/b'],
                                     ['https://cdn.modrinth.com/a?token=secret'], ['http://cdn.modrinth.com/a'],
                                     ['https://cdn.modrinth.com/'], ['https://cdn.modrinth.com:8443/a']]):
            row.update(officialOnly=True, sources=sources)
            with self.subTest(sources=sources), self.assertRaises(builder.BuildError):
                self.build(str(i))
        row.update(officialOnly='true', sources=['https://cdn.modrinth.com/a'])
        with self.assertRaisesRegex(builder.BuildError, 'boolean'):
            self.build('invalid-bool')

    def test_runtime_must_always_be_bundled(self):
        self.manifest['runtime']['archive'].update(officialOnly=True, sources=['https://cdn.modrinth.com/a'])
        self.fail('Runtime.Archive cannot')

    def test_official_bytes_cannot_be_packed_under_an_unmarked_alias(self):
        row = self.manifest['files'][0]
        self.manifest['files'].append({**row, 'path': 'mods/renamed.jar'})
        row.update(officialOnly=True, sources=['https://cdn.modrinth.com/data/pinned.jar'])
        self.fail('another manifest path')

    def test_inventory_reuses_exact_archive_entry_under_explicit_root(self):
        row = self.manifest['files'][1]
        source = self.cache / row['sha256']
        archive_path = self.cache / 'retained-overrides.zip'
        with zipfile.ZipFile(archive_path, 'w') as archive:
            archive.writestr('overrides/' + row['path'], source.read_bytes())
        source.unlink()
        inventory = self.root / 'inventory.json'
        inventory.write_bytes(builder.json_bytes({'objects': [{'sha256': row['sha256'], 'size': row['size'],
            'localMatches': [{'path': str(archive_path), 'kind': 'archive-entry', 'entry': 'overrides/' + row['path']}]}]}))
        self.assertEqual(self.build(inventory=inventory)['sourceReferences']['local-archive-entry'], 1)

    def test_inventory_outside_explicit_roots_is_not_opened(self):
        row = self.manifest['files'][0]
        outside = self.root / 'not-allowed.jar'
        (self.cache / row['sha256']).rename(outside)
        inventory = self.root / 'inventory.json'
        inventory.write_bytes(builder.json_bytes({'objects': [{'sha256': row['sha256'], 'size': row['size'],
            'localMatches': [{'path': str(outside), 'kind': 'file'}]}]}))
        self.fail('Required object unavailable', inventory=inventory)

    def test_origin_hash_layout_and_source_cache_suffix_are_supported(self):
        for i, row in enumerate([*self.manifest['files'], self.manifest['runtime']['archive']]):
            destination = self.cache / ('objects/sha256' if i % 2 else '') / (row['sha256'] + ('.jar' if i % 2 == 0 else ''))
            destination.parent.mkdir(parents=True, exist_ok=True)
            (self.cache / row['sha256']).rename(destination)
        self.assertTrue(self.build()['candidate'])

    def test_no_network_unless_download_base_is_explicit(self):
        (self.cache / self.manifest['files'][0]['sha256']).unlink()
        with mock.patch.object(builder.urllib.request, 'build_opener') as opener:
            self.fail('Required object unavailable')
            opener.assert_not_called()

    def test_opt_in_download_uses_only_hash_endpoint_and_checks_hash(self):
        row = self.manifest['files'][0]
        data = (self.cache / row['sha256']).read_bytes()
        (self.cache / row['sha256']).unlink()
        opener = mock.Mock()
        opener.open.return_value = io.BytesIO(data)
        with mock.patch.object(builder.urllib.request, 'build_opener', return_value=opener):
            self.assertEqual(self.build(download_base=self.url)['sourceReferences']['download'], 1)
        request = opener.open.call_args.args[0]
        self.assertEqual(request.full_url, self.url + '/' + row['sha256'])
        self.assertNotIn('Authorization', request.headers)

    def test_bad_download_fails_hash(self):
        row = self.manifest['files'][0]
        (self.cache / row['sha256']).unlink()
        opener = mock.Mock()
        opener.open.return_value = io.BytesIO(b'x' * row['size'])
        with mock.patch.object(builder.urllib.request, 'build_opener', return_value=opener):
            self.fail('SHA256/size mismatch', download_base=self.url)

    def test_download_errors_do_not_echo_credential_urls(self):
        row = self.manifest['files'][0]
        (self.cache / row['sha256']).unlink()
        secret = 'SyntheticSecret123456789'
        opener = mock.Mock()
        opener.open.side_effect = builder.urllib.error.URLError('https://bad.example/a?token=' + secret)
        with mock.patch.object(builder.urllib.request, 'build_opener', return_value=opener):
            report = self.fail('Object download failed', download_base=self.url)
        self.assertNotIn(secret, json.dumps(report))

    def test_credential_redirect_is_rejected(self):
        row = self.manifest['files'][0]
        data = (self.cache / row['sha256']).read_bytes()
        (self.cache / row['sha256']).unlink()
        opener = mock.Mock()
        opener.open.return_value = io.BytesIO(data)
        with mock.patch.object(builder.urllib.request, 'build_opener', return_value=opener) as make_opener:
            self.build(download_base=self.url)
        redirect = make_opener.call_args.args[0]
        with self.assertRaises(builder.BuildError):
            redirect.redirect_request(builder.urllib.request.Request(self.url + '/object'), None, 302, 'Found', {},
                                      'https://cdn.example/object?token=SyntheticSecret123456789')

    def test_zip_verifier_rejects_missing_extra_duplicate_and_official_entries(self):
        expected = builder.archive_rows(self.manifest)
        for case in ('missing', 'extra', 'duplicate', 'official'):
            path = self.root / (case + '.zip')
            manifest = copy.deepcopy(self.manifest)
            if case == 'official':
                manifest['files'][0]['officialOnly'] = True
            with zipfile.ZipFile(path, 'w') as archive:
                for i, (name, row) in enumerate(expected.items()):
                    if case == 'missing' and i == 0:
                        continue
                    archive.writestr(name, (self.cache / row['sha256']).read_bytes())
                if case == 'extra':
                    archive.writestr('extras.txt', b'extra')
                if case == 'duplicate':
                    archive.writestr('MODS/example.jar', b'bad')
            with self.subTest(case=case), self.assertRaises(builder.BuildError):
                builder.verify_bundle(path, manifest)

    def test_symlink_source_is_not_followed(self):
        row = self.manifest['files'][0]
        source = self.cache / row['sha256']
        target = self.root / 'outside.jar'
        source.rename(target)
        try:
            source.symlink_to(target)
        except OSError:
            self.skipTest('OS does not permit symlink creation')
        self.fail('link or reparse point')

    def test_duplicate_json_keys_are_rejected(self):
        self.manifest_path.write_text('{"files":[],"files":[]}', encoding='utf-8')
        with self.assertRaisesRegex(builder.BuildError, 'Duplicate JSON'):
            builder.build(self.manifest_path, self.root / 'output', 'new', 2, public_base=self.url)

    def test_metadata_write_failure_removes_partial_candidate_outputs(self):
        real_link = builder.os.link
        calls = 0
        def fail_second(source, target):
            nonlocal calls
            calls += 1
            if calls == 2:
                raise OSError('Synthetic failure')
            return real_link(source, target)
        with mock.patch.object(builder.os, 'link', side_effect=fail_second):
            self.fail('Build failed: OSError')

    def test_cli_writes_unsigned_candidate_with_explicit_inputs(self):
        process = subprocess.run([sys.executable, '-X', 'utf8', str(ROOT / 'tools/build-complete-client.py'),
            '--manifest', str(self.manifest_path), '--output', str(self.root / 'cli'), '--version', 'candidate.2',
            '--sequence', '2', '--object-root', str(self.cache), '--public-object-base', self.url],
            capture_output=True, text=True, encoding='utf-8')
        self.assertEqual(process.returncode, 0, process.stdout + process.stderr)
        self.assertFalse(json.loads(process.stdout)['uploaded'])


if __name__ == '__main__':
    unittest.main()

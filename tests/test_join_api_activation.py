"""Scoped filesystem test: forced deployment failure restores files and retains the first snapshot."""
import importlib.util
import json
import pathlib
import subprocess
import sys
import tempfile
import types
import unittest

REPO = pathlib.Path(__file__).resolve().parents[1]
if sys.platform == 'win32':
    sys.modules.setdefault('fcntl', types.SimpleNamespace(LOCK_EX=1, LOCK_NB=2, flock=lambda *_: None))
spec = importlib.util.spec_from_file_location('join_activation', REPO / 'deploy/activate-join-api.py')
deploy = importlib.util.module_from_spec(spec)
spec.loader.exec_module(deploy)


class ActivationTests(unittest.TestCase):
    def test_prepare_time_guard_and_failed_upgrade_restore(self):
        local = REPO / '.local'
        local.mkdir(exist_ok=True)
        with tempfile.TemporaryDirectory(prefix='join-activation-test-', dir=local) as directory:
            root = pathlib.Path(directory).resolve()
            self.assertTrue(root.is_relative_to(local.resolve()))
            deploy.ROOT = root
            deploy.STAGE = root / 'staging/join-auth-1.0.0'
            deploy.KEY_FILE = deploy.STAGE / 'server-keys.json'
            deploy.STATE_FILE = deploy.STAGE / 'state.json'
            deploy.BACKUP_ROOT = root / 'volume-backups'
            if not hasattr(deploy.os, 'chown'):
                deploy.os.chown = lambda *_: None
            deploy.private_directory(deploy.STAGE)
            keys, state = deploy.prepare()
            again, _ = deploy.prepare()
            self.assertEqual(keys, again)
            self.assertEqual(4, len(set(keys.values())))
            self.assertFalse((root / 'secrets/api.env').exists())
            self.assertEqual('Other=x\nJoinAuth__Enabled=true\n', deploy.patch_environment('Other=x\nJoinAuth__Enabled=false\nJoinAuth__Enabled=false\n', {'JoinAuth__Enabled': 'true'}))
            deploy.utcnow = lambda: deploy.NOT_BEFORE - deploy.dt.timedelta(seconds=1)
            with self.assertRaisesRegex(RuntimeError, 'Activation is authorized only from'):
                deploy.activate(keys, state)
            deploy.utcnow = lambda: deploy.NOT_BEFORE + deploy.dt.timedelta(seconds=1)
            for folder in ('secrets', 'api', 'releases/api-1.1.0/api', 'volume-backups'):
                (root / folder).mkdir(parents=True, exist_ok=True)
            old_env = 'ConnectionStrings__Hub=private-value\nUnrelated=keep\n'
            old_compose = 'image: boshan/hub-api:0.1.6\n'
            old_nginx = 'server {\n    location / { try_files $uri =404; }\n}\n'
            (root / 'secrets/api.env').write_text(old_env)
            (root / 'compose.yml').write_text(old_compose)
            (root / 'direct-tls.nginx.conf').write_text(old_nginx)
            (root / 'api/Hub.Api.dll').write_bytes(b'old-api')
            (root / 'releases/api-1.1.0/api/Hub.Api.dll').write_bytes(b'new-api')
            (root / 'upgrade-api.py').write_text('# isolated fixture')
            game = {'Id': 'same-game-container', 'State': {'Running': True, 'StartedAt': 'unchanged'}, 'NetworkSettings': {'Networks': {}}}
            calls = []

            def fake_run(*args):
                calls.append(args)
                if args[:2] == ('docker', 'inspect'):
                    value = game if args[2] == 'gsmanager' else {'Mounts': [{'Source': str(root / 'direct-tls.nginx.conf'), 'Destination': '/etc/nginx/conf.d/default.conf'}]}
                    return json.dumps([value])
                if args[:3] == ('docker', 'compose', 'ps'):
                    return 'gateway-id\n'
                if args[:3] == ('docker', 'network', 'connect'):
                    game['NetworkSettings']['Networks'][deploy.EDGE] = {'IPAddress': '172.25.0.44'}
                elif args[:3] == ('docker', 'network', 'disconnect'):
                    game['NetworkSettings']['Networks'].pop(deploy.EDGE)
                elif args == ('sh', 'backup.sh'):
                    (deploy.BACKUP_ROOT / 'hub-20260906T070001Z.dump').write_bytes(b'old-database')
                elif len(args) > 1 and args[1] == str(root / 'upgrade-api.py'):
                    (root / 'api/Hub.Api.dll').write_bytes(b'bad-new-api')
                    (root / 'compose.yml').write_text('image: boshan/hub-api:1.1.0\n')
                    raise subprocess.CalledProcessError(1, ['isolated-failure'])
                return ''

            deploy.run = fake_run
            deploy.wait_health = lambda: True
            result = deploy.activate(keys, state)
            self.assertEqual('failed-restored', result['state'])
            self.assertEqual('upgrade-api', result['failedPhase'])
            self.assertEqual(old_env, (root / 'secrets/api.env').read_text())
            self.assertEqual(old_compose, (root / 'compose.yml').read_text())
            self.assertEqual(old_nginx, (root / 'direct-tls.nginx.conf').read_text())
            self.assertEqual(b'old-api', (root / 'api/Hub.Api.dll').read_bytes())
            self.assertEqual({}, game['NetworkSettings']['Networks'])
            self.assertFalse(any('restart' in command for command in calls))
            first = pathlib.Path(result['firstBackup'])
            self.assertEqual(old_env, (first / 'api.env').read_text())
            second = deploy.activate(keys, json.loads(deploy.STATE_FILE.read_text()))
            self.assertEqual(str(first), second['firstBackup'])
            self.assertNotEqual(first, pathlib.Path(second['attemptBackup']))
            self.assertEqual(old_env, (first / 'api.env').read_text())


if __name__ == '__main__':
    unittest.main()

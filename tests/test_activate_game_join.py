"""Local-only checks: never invoke Docker, RCON, a production path or a real restart."""
import datetime as dt
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
import zipfile

spec = importlib.util.spec_from_file_location('activate_game_join', Path(__file__).resolve().parents[1] / 'deploy/activate-game-join.py')
deployment = importlib.util.module_from_spec(spec)
spec.loader.exec_module(deployment)


class JoinDeploymentTests(unittest.TestCase):
    def test_injection_changes_only_real_game_invocation_and_preserves_memory_and_probes(self):
        for id, server in deployment.SERVERS.items():
            old = server['command']
            source = ('#!/bin/bash\nJAVA_VERSION=$("$JAVA_CMD" -version 2>&1 | head -n 1)\n'
                      + 'echo "diagnostic ' + old + '"\n    ' + old + ' -Xmx8096M -Dkeep=value nogui\n').encode()
            changed = deployment.inject(source, id, 'a' * 64)
            self.assertIn(b'JAVA_VERSION=$("$JAVA_CMD" -version 2>&1 | head -n 1)', changed)
            self.assertIn(('echo "diagnostic ' + old + '"').encode(), changed)
            self.assertEqual(1, changed.count(b'-javaagent:'))
            self.assertEqual(source.count(b'-Xmx8096M'), changed.count(b'-Xmx8096M'))
            self.assertIn(b' -Dkeep=value nogui\n', changed)
            self.assertNotIn(b'JAVA_TOOL_OPTIONS', changed)

    def test_ambiguous_or_already_modified_script_refused(self):
        line = deployment.SERVERS['mb']['command']
        for source in ('#!/bin/bash\n' + line + '\n' + line + '\n', '#!/bin/bash\n# mojin.join.server.config\n' + line):
            with self.assertRaises(RuntimeError):
                deployment.inject(source.encode(), 'mb', 'a' * 64)

    def test_prepare_only_changes_staging_and_records_no_secret(self):
        with tempfile.TemporaryDirectory() as temporary:
            base = Path(temporary)
            stage, games = base / 'stage', base / 'games'
            stage.mkdir()
            keys = {id: str(index) * 64 for index, id in enumerate(deployment.SERVERS, 1)}
            (stage / 'server-keys.json').write_text(json.dumps(keys))
            (stage / 'server-keys.json').chmod(0o600)
            servers, original = {}, {}
            for id, server in deployment.SERVERS.items():
                row = server.copy()
                root = games / row['directory']
                root.mkdir(parents=True)
                content = ('#!/bin/bash\n' + row['command'] + ' nogui\n').encode()
                (root / row['script']).write_bytes(content)
                original[root / row['script']] = content
                row['scriptHash'] = deployment.sha(content)
                if row.get('outerScript'):
                    outer = b'#!/bin/bash\nexec ./ServerStart.sh\n'
                    (root / row['outerScript']).write_bytes(outer)
                    original[root / row['outerScript']] = outer
                    row['outerHash'] = deployment.sha(outer)
                servers[id] = row
            jar = base / 'agent.jar'
            with zipfile.ZipFile(jar, 'w') as archive:
                archive.writestr('META-INF/MANIFEST.MF', b'Manifest-Version: 1.0\nPremain-Class: fixture.Agent\n')
            with patch.object(deployment, 'STAGING', stage), patch.object(deployment, 'HOST_GAMES', games), patch.object(deployment, 'SERVERS', servers), patch.object(deployment, 'inspect_environment'), patch.object(deployment, 'command'):
                deployment.prepare(jar)
                deployment.prepare(jar)  # Idempotent held preparation.
                old_first_candidate = (stage / 'servers/m3e/candidate/agent.jar').read_bytes()
                with zipfile.ZipFile(jar, 'a') as archive:
                    archive.writestr('replacement.txt', b'final ASM update')
                last_state = stage / 'servers/vw/state.json'
                last = json.loads(last_state.read_text())
                last_state.write_text(json.dumps(dict(last, phase='active')))
                with self.assertRaisesRegex(RuntimeError, 'existing activation state'):
                    deployment.prepare(jar)
                self.assertEqual(old_first_candidate, (stage / 'servers/m3e/candidate/agent.jar').read_bytes())
                last_state.write_text(json.dumps(last))
                deployment.prepare(jar)
                for id in servers:
                    self.assertEqual(deployment.sha(jar.read_bytes()), json.loads((stage / 'servers' / id / 'plan.json').read_text())['agentSha256'])
            for path, content in original.items():
                self.assertEqual(content, path.read_bytes())
                self.assertFalse((path.parent / '.join-auth').exists())
            for id in servers:
                work = stage / 'servers' / id
                plan = (work / 'plan.json').read_text()
                state = (work / 'state.json').read_text()
                self.assertEqual('hold', json.loads(plan)['phase'])
                self.assertFalse(json.loads(plan)['activationAllowed'])
                self.assertEqual('prepared', json.loads(state)['phase'])
                self.assertNotIn(keys[id], plan + state)
                self.assertIn('secret=' + keys[id], (work / 'candidate/server.properties').read_text())
                self.assertTrue((work / 'candidate/server.properties').read_text().startswith('mode=observe\n'))

    def test_activation_time_guard_runs_before_docker_or_any_file_write(self):
        future = dt.datetime.now(dt.timezone.utc) + dt.timedelta(days=1)
        with patch.object(deployment, 'NOT_BEFORE', future), patch.object(deployment, 'inspect_environment') as inspect:
            with self.assertRaisesRegex(RuntimeError, 'held until'):
                deployment.activate('vw')
            inspect.assert_not_called()

    def test_missing_real_api_activation_cannot_be_replaced_by_a_fake_ready_file(self):
        with tempfile.TemporaryDirectory() as temporary:
            stage = Path(temporary)
            (stage / 'state.json').write_text(json.dumps({'state': 'prepared', 'apiVersion': '1.1.0'}))
            (stage / 'ready.json').write_text(json.dumps({'apiReady': True, 'agentSha256': 'a' * 64}))
            with patch.object(deployment, 'STAGING', stage), patch.object(deployment, 'command') as command:
                with self.assertRaisesRegex(RuntimeError, 'real API activation'):
                    deployment.api_ready({'instance': 'vw', 'agentSha256': 'a' * 64})
                command.assert_not_called()

    def test_hot_mode_changes_preserve_configuration_and_never_restart(self):
        with tempfile.TemporaryDirectory() as temporary:
            base = Path(temporary)
            games, work, proc = base / 'games', base / 'work', base / 'proc'
            root = games / deployment.SERVERS['vw']['directory']
            auth = root / '.join-auth'
            auth.mkdir(parents=True)
            work.mkdir()
            (proc / '123').mkdir(parents=True)
            agent = b'fixture-agent'
            agent_hash = deployment.sha(agent)
            (auth / ('agent-' + agent_hash + '.jar')).write_bytes(agent)
            script = root / deployment.SERVERS['vw']['script']
            script.write_bytes(b'fixture-game-start')
            original = b'mode=observe\ninstance=vw\nsecret=kept-private\nredeemUrl=http://hub-api:8080/internal/v1/join/redeem\n'
            config = auth / 'server.properties'
            config.write_bytes(original)
            before = config.stat().st_mtime_ns
            process = {'pid': 123, 'startedTicks': '321'}
            plan = {'instance': 'vw', 'agentSha256': agent_hash, 'configSha256': deployment.sha(original), 'candidateScriptSha256': deployment.sha(script.read_bytes())}
            state = {'instance': 'vw', 'phase': 'active', 'mode': 'observe', 'process': process, 'runtimeConfigSha256': deployment.sha(original)}
            (work / 'state.json').write_text(json.dumps(state))
            expected = f'-javaagent:{deployment.CONTAINER_GAMES / deployment.SERVERS["vw"]["directory"]}/.join-auth/agent-{agent_hash}.jar'
            (proc / '123/cmdline').write_bytes(b'java\0' + expected.encode() + b'\0')
            with patch.object(deployment, 'NOT_BEFORE', dt.datetime(2000, 1, 1, tzinfo=dt.timezone.utc)), patch.object(deployment, 'HOST_GAMES', games), patch.object(deployment, 'PROC_ROOT', proc), patch.object(deployment, 'inspect_environment'), patch.object(deployment, 'validate_candidate', return_value=(work, plan)), patch.object(deployment, 'api_ready'), patch.object(deployment, 'game_processes', return_value=[process]), patch.object(deployment, 'gsm') as gsm:
                deployment.set_mode('vw', 'enforce')
                self.assertEqual(original.replace(b'mode=observe', b'mode=enforce'), config.read_bytes())
                self.assertGreater(config.stat().st_mtime_ns, before)
                self.assertEqual('enforce', json.loads((work / 'state.json').read_text())['mode'])
                deployment.set_mode('vw', 'observe')
                self.assertEqual(original, config.read_bytes())
                gsm.assert_not_called()


if __name__ == '__main__':
    unittest.main()

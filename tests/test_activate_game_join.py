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

    def test_dc2_has_independent_time_guard_for_activation_and_rollback(self):
        future = dt.datetime.now(dt.timezone.utc) + dt.timedelta(days=1)
        with patch.object(deployment, 'NOT_BEFORE', dt.datetime(2000, 1, 1, tzinfo=dt.timezone.utc)), patch.object(deployment, 'DC2_NOT_BEFORE', future), patch.object(deployment, 'inspect_environment') as inspect:
            deployment.time_guard('m3e')
            for operation in (deployment.activate, deployment.rollback):
                with self.assertRaisesRegex(RuntimeError, 'held until'):
                    operation('dc2')
            inspect.assert_not_called()

    def test_java_version_probe_is_not_a_game_process(self):
        with tempfile.TemporaryDirectory() as temporary:
            proc = Path(temporary)
            directory = proc / '123'
            directory.mkdir()
            (directory / 'comm').write_text('java')
            (directory / 'cmdline').write_bytes(b'java\0-version\0')
            (directory / 'stat').write_text('123 (java) ' + ' '.join(['0'] * 20))
            expected = str(deployment.CONTAINER_GAMES / deployment.SERVERS['m3e']['directory'])
            with patch.object(deployment, 'PROC_ROOT', proc), patch.object(deployment.os, 'readlink', return_value=expected):
                self.assertEqual([], deployment.game_processes('m3e'))
                (directory / 'cmdline').write_bytes(b'java\0-jar\0server.jar\0')
                self.assertEqual([{'pid':123, 'startedTicks':'0'}], deployment.game_processes('m3e'))

    def test_idle_shell_does_not_block_stop_but_start_wrapper_does(self):
        with tempfile.TemporaryDirectory() as temporary:
            proc = Path(temporary)
            directory = proc / '123'
            directory.mkdir()
            (directory / 'comm').write_text('bash')
            (directory / 'cmdline').write_bytes(b'bash\0')
            expected = str(deployment.CONTAINER_GAMES / deployment.SERVERS['mb']['directory'])
            with patch.object(deployment, 'PROC_ROOT', proc), patch.object(deployment.os, 'readlink', return_value=expected):
                self.assertEqual([], deployment.workdir_processes('mb'))
                (directory / 'cmdline').write_bytes(b'bash\0ServerStart.sh\0')
                self.assertEqual([123], deployment.workdir_processes('mb'))

    def test_missing_real_api_activation_cannot_be_replaced_by_a_fake_ready_file(self):
        with tempfile.TemporaryDirectory() as temporary:
            stage = Path(temporary)
            (stage / 'state.json').write_text(json.dumps({'state': 'prepared', 'apiVersion': '1.1.0'}))
            (stage / 'ready.json').write_text(json.dumps({'apiReady': True, 'agentSha256': 'a' * 64}))
            with patch.object(deployment, 'STAGING', stage), patch.object(deployment, 'command') as command:
                with self.assertRaisesRegex(RuntimeError, 'real API activation'):
                    deployment.api_ready({'instance': 'vw', 'agentSha256': 'a' * 64})
                command.assert_not_called()

    def test_single_server_prepare_preserves_current_generation_and_first_backup(self):
        with tempfile.TemporaryDirectory() as temporary:
            base = Path(temporary)
            stage = base / 'stage'
            work = stage / 'servers/mb'
            (work / 'candidate').mkdir(parents=True)
            (work / 'backup').mkdir()
            original = ('#!/bin/bash\n' + deployment.SERVERS['mb']['command'] + ' nogui\n').encode()
            (work / 'backup/ServerStart.sh').write_bytes(original)
            (work / 'candidate/agent.jar').write_bytes(b'old agent')
            old = {'instance':'mb','agentSha256':'a'*64,'candidateScriptSha256':'b'*64,'originalScriptSha256':deployment.sha(original)}
            (work / 'plan.json').write_text(json.dumps(old))
            (work / 'state.json').write_text(json.dumps({'phase':'active','mode':'observe'}))
            preserved = {p:p.read_bytes() for p in work.rglob('*') if p.is_file()}
            other = stage / 'servers/m3e/candidate'
            other.mkdir(parents=True)
            (other / 'agent.jar').write_bytes(b'leave other server alone')
            jar = base / 'new.jar'
            with zipfile.ZipFile(jar,'w') as z:
                z.writestr('META-INF/MANIFEST.MF','Premain-Class: example.Agent\n')
            state = {'phase':'active','process':{'pid':123,'startedTicks':'456'}}
            with patch.object(deployment,'inspect_environment'), patch.object(deployment,'validate_candidate',return_value=(work,old)), patch.object(deployment,'validate_live_generation',return_value=state), patch.object(deployment,'private_keys',return_value={'mb':'private-key'}), patch.object(deployment,'command'):
                deployment.prepare_server('mb',jar)
            for p, data in preserved.items():
                self.assertEqual(data,p.read_bytes())
            self.assertEqual(b'leave other server alone',(other/'agent.jar').read_bytes())
            pending=json.loads((work/'pending-plan.json').read_text())
            self.assertEqual(deployment.sha(jar.read_bytes()),pending['agentSha256'])
            self.assertEqual('observe',pending['initialMode'])
            self.assertNotIn('private-key',(work/'pending-plan.json').read_text())
            self.assertEqual(original.count(b'nogui'),(work/'pending/ServerStart.sh').read_bytes().count(b'nogui'))

    def test_single_server_activation_refuses_changed_source_before_stop(self):
        with tempfile.TemporaryDirectory() as temporary:
            work=Path(temporary)
            (work/'plan.json').write_text('{}')
            (work/'pending-plan.json').write_text(json.dumps({'instance':'mb','sourcePlanSha256':'a'*64}))
            with patch.object(deployment,'time_guard'), patch.object(deployment,'inspect_environment'), patch.object(deployment,'validate_candidate',return_value=(work,{})), patch.object(deployment,'validate_live_generation',return_value={'phase':'active'}), patch.object(deployment,'stop_game') as stop:
                with self.assertRaisesRegex(RuntimeError,'no longer matches'):
                    deployment.activate_server('mb')
                stop.assert_not_called()

    def test_single_server_activation_from_maintenance_preserves_first_backup(self):
        with tempfile.TemporaryDirectory() as temporary:
            base=Path(temporary)
            games,work=base/'games',base/'work'
            root=games/deployment.SERVERS['dc2']['directory']
            root.mkdir(parents=True)
            for folder in ('candidate','pending','backup'):
                (work/folder).mkdir(parents=True)
            original=b'#!/bin/bash\nexec java -Xmx8G -jar server.jar\n'
            old_script=b'old agent command'
            new_script=b'new pinned agent command -Xmx8G'
            (root/'run.sh').write_bytes(original)
            (work/'backup/run.sh').write_bytes(original)
            metadata={'gameUid':0,'gameGid':0,'scriptUid':0,'scriptGid':0,'scriptMode':0o755}
            (work/'backup/metadata.json').write_text(json.dumps(metadata))
            old={'instance':'dc2','agentSha256':deployment.sha(b'old'),'originalScriptSha256':deployment.sha(original),'candidateScriptSha256':deployment.sha(old_script)}
            (work/'plan.json').write_text(json.dumps(old))
            state={'instance':'dc2','phase':'maintenance'}
            (work/'state.json').write_text(json.dumps(state))
            for name,data in {'agent.jar':b'old','server.properties':b'old config','run.sh':old_script}.items():
                (work/'candidate'/name).write_bytes(data)
            props=b'mode=observe\nsecret=private-key\n'
            for name,data in {'agent.jar':b'new','server.properties':props,'run.sh':new_script}.items():
                (work/'pending'/name).write_bytes(data)
            plan=dict(old,agentSha256=deployment.sha(b'new'),candidateScriptSha256=deployment.sha(new_script),configSha256=deployment.sha(props),sourcePlanSha256=deployment.sha((work/'plan.json').read_bytes()),sourcePhase='maintenance',sourceProcess=None)
            (work/'pending-plan.json').write_text(json.dumps(plan))
            backups={p.name:p.read_bytes() for p in (work/'backup').iterdir()}
            new_process={'pid':321,'startedTicks':'654'}
            with patch.object(deployment,'HOST_GAMES',games), patch.object(deployment,'time_guard'), patch.object(deployment,'inspect_environment'), patch.object(deployment,'validate_candidate',return_value=(work,old)), patch.object(deployment,'validate_live_generation',return_value=state), patch.object(deployment,'private_keys',return_value={'dc2':'private-key'}), patch.object(deployment,'api_ready'), patch.object(deployment,'game_processes',return_value=[]), patch.object(deployment,'stop_game') as stop, patch.object(deployment,'rcon') as rcon, patch.object(deployment,'start_game',return_value=new_process) as start, patch.object(deployment.os,'chown',create=True):
                deployment.activate_server('dc2')
            stop.assert_called_once_with('dc2')
            start.assert_called_once_with('dc2',plan['agentSha256'])
            rcon.assert_not_called()
            self.assertEqual(backups,{p.name:p.read_bytes() for p in (work/'backup').iterdir()})
            self.assertEqual(new_script,(root/'run.sh').read_bytes())
            self.assertEqual(props,(root/'.join-auth/server.properties').read_bytes())
            self.assertEqual('observe',json.loads((work/'state.json').read_text())['mode'])
            self.assertEqual(1,len(list((work/'generations').iterdir())))

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

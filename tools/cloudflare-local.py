"""Use the user's local Cloudflare credentials without putting them in logs or argv."""
import argparse
import json
import os
from pathlib import Path
import re
import sys
import urllib.error
import urllib.parse
import urllib.request


class Cloudflare:
    def __init__(self, path):
        try:
            self.config = json.loads(path.read_text(encoding='utf-8-sig'))
        except (OSError, ValueError):
            raise SystemExit('Local credential file is missing or invalid; no values displayed.') from None
        key = self.config.get('globalApiKey', '').strip()
        email = self.config.get('email', '').strip()
        self.account = self.config.get('accountId', '').strip()
        if not key or not email:
            raise SystemExit('Fill globalApiKey in the local credentials.json file first.')
        if not re.fullmatch(r'[a-f0-9]{32}', self.account):
            raise SystemExit('Local accountId must be a 32-character Cloudflare account identifier.')
        self.headers = {'X-Auth-Key': key, 'X-Auth-Email': email, 'Content-Type': 'application/json'}
        self.base = 'accounts/' + self.account + '/cfd_tunnel'
        # Credentials must never follow a redirect to a different origin.
        class NoRedirect(urllib.request.HTTPRedirectHandler):
            def redirect_request(self, req, fp, code, msg, headers, newurl):
                return None
        self.opener = urllib.request.build_opener(NoRedirect)

    def api(self, method, path, body=None):
        operation = method + ' ' + path.split('?')[0]
        request = urllib.request.Request('https://api.cloudflare.com/client/v4/' + path,
            data=None if body is None else json.dumps(body).encode(), method=method, headers=self.headers)
        try:
            with self.opener.open(request, timeout=30) as response:
                result = json.load(response)
        except urllib.error.HTTPError as error:
            raise SystemExit('Cloudflare operation failed: ' + operation + ' HTTP ' + str(error.code)) from None
        except (OSError, ValueError):
            raise SystemExit('Cloudflare network or response error at ' + operation + '; no credential values displayed.') from None
        if not result.get('success'):
            raise SystemExit('Cloudflare operation failed; no response body displayed.')
        return result['result']

    def zone(self):
        matches = self.api('GET', 'zones?name=boshan.uk&account.id=' + self.account)
        if len(matches) != 1 or matches[0]['name'] != 'boshan.uk':
            raise SystemExit('Expected boshan.uk in the configured account; no resources changed.')
        return matches[0]['id']

    def inspect(self):
        zone = self.zone()
        tunnels = self.api('GET', self.base + '?is_deleted=false')
        records = self.api('GET', 'zones/' + zone + '/dns_records?name=launcher.boshan.uk')
        print(json.dumps({'authentication': 'local-global-key', 'zoneAccess': True,
            'tunnelCount': len(tunnels), 'launcherDnsCount': len(records)}))

    def native_api(self):
        zone = self.zone()
        base = 'zones/' + zone + '/rulesets'
        matches = [item for item in self.api('GET', base)
                   if item['kind'] == 'zone' and item['phase'] == 'http_config_settings']
        if len(matches) > 1:
            raise SystemExit('Multiple configuration entrypoints; no rules changed.')
        rule = {'ref': 'mojin_launcher_native_api', 'description': 'Mojin Dashuai native account API',
            'expression': '(http.host eq "launcher.boshan.uk")', 'action': 'set_config',
            'action_parameters': {'bic': False}, 'enabled': True}
        if not matches:
            self.api('POST', base, {'name': 'Zone configuration settings', 'kind': 'zone',
                'phase': 'http_config_settings', 'rules': [rule]})
        else:
            ruleset_id = matches[0]['id']
            existing = self.api('GET', base + '/' + ruleset_id)
            own = [item for item in existing.get('rules', []) if item.get('ref') == rule['ref']]
            if len(own) > 1:
                raise SystemExit('Duplicate launcher rule; no rules changed.')
            if own:
                self.api('PATCH', base + '/' + ruleset_id + '/rules/' + own[0]['id'], rule)
            else:
                self.api('POST', base + '/' + ruleset_id + '/rules', rule)
        print(json.dumps({'nativeApiHostname': 'launcher.boshan.uk', 'browserIntegrityCheck': False,
            'otherRulesPreserved': True}))

    def provision(self, output):
        # Check both the receiving location and existing DNS before changing any remote resources.
        output = output.resolve()
        credential_root = Path('D:/project/cfapi')
        if not output.is_relative_to(credential_root.resolve()) or output == credential_root.resolve():
            raise SystemExit('Tunnel credentials must be written under the local D:/project/cfapi directory.')
        output.parent.mkdir(parents=True, exist_ok=True)
        if output.exists() and output.is_symlink():
            raise SystemExit('Refusing a symlink credential output.')
        zone = self.zone()
        records_path = 'zones/' + zone + '/dns_records'
        records = self.api('GET', records_path + '?name=launcher.boshan.uk')
        matches = [item for item in self.api('GET', self.base + '?is_deleted=false&name=boshan-client-hub')
                   if item['name'] == 'boshan-client-hub']
        if len(matches) > 1:
            raise SystemExit('Multiple matching tunnels; no resources changed.')
        if records and (len(records) != 1 or not matches or records[0]['type'] != 'CNAME'
                        or records[0]['content'] != matches[0]['id'] + '.cfargotunnel.com'):
            raise SystemExit('Hostname exists with another target; no resources changed.')
        if not matches:
            import base64
            tunnel = self.api('POST', self.base, {'name': 'boshan-client-hub', 'config_src': 'cloudflare',
                'tunnel_secret': base64.b64encode(os.urandom(32)).decode()})
        else:
            tunnel = matches[0]
        tunnel_id = tunnel['id']
        self.api('PUT', self.base + '/' + tunnel_id + '/configurations', {'config': {'ingress': [
            {'hostname': 'launcher.boshan.uk', 'service': 'http://hub-api:8080'}, {'service': 'http_status:404'}]}})
        if not records:
            self.api('POST', records_path, {'type': 'CNAME', 'name': 'launcher.boshan.uk',
                'content': tunnel_id + '.cfargotunnel.com', 'proxied': True, 'ttl': 1,
                'comment': 'Mojin Dashuai account API'})
        token = self.api('GET', self.base + '/' + tunnel_id + '/token')
        temp = output.with_name(output.name + '.tmp')
        if temp.exists():
            raise SystemExit('Credential staging file already exists; inspect it locally before retrying.')
        with temp.open('x', encoding='utf-8') as stream:
            os.chmod(temp, 0o600)
            stream.write(token)
            stream.flush()
            os.fsync(stream.fileno())
        temp.replace(output)
        print(json.dumps({'tunnelId': tunnel_id, 'hostname': 'launcher.boshan.uk', 'credentialSaved': True}))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('command', choices=['inspect', 'provision-launcher', 'configure-native-api'])
    parser.add_argument('--credentials', type=Path, default=Path('D:/project/cfapi/credentials.json'))
    parser.add_argument('--tunnel-output', type=Path, default=Path('D:/project/cfapi/mojin-tunnel-token.txt'))
    args = parser.parse_args()
    client = Cloudflare(args.credentials)
    if args.command == 'inspect':
        client.inspect()
    elif args.command == 'configure-native-api':
        client.native_api()
    else:
        client.provision(args.tunnel_output)


if __name__ == '__main__':
    try:
        main()
    except KeyboardInterrupt:
        sys.exit('Cancelled.')
    except Exception:
        sys.exit('Cloudflare operation did not complete; no request or credential values displayed.')

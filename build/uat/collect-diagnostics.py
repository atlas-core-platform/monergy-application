"""Read-only, redacted diagnostics from one existing local UAT profile.
Never emits compose configuration, inspect environment, or the private profile.
"""
import argparse
import json
import pathlib
import re
import subprocess


def secrets_in(value, sensitive=False):
    if isinstance(value, dict):
        for key, child in value.items():
            yield from secrets_in(child, sensitive or bool(re.search(r'password|token|secret', key, re.I)))
    elif isinstance(value, list):
        for child in value:
            yield from secrets_in(child, sensitive)
    elif sensitive and isinstance(value, str) and value:
        yield value


def redact(text, secrets):
    for value in sorted(set(secrets), key=len, reverse=True):
        text = text.replace(value, '[REDACTED]')
    # Include transient session/bearer credentials that are not in profile.json.
    text = re.sub(r'(?i)(Bearer\s+)[^\s";,]+', r'\1[REDACTED]', text)
    text = re.sub(r'(?i)((?:password|authenticationContextId|X-Monergy-Session|X-Monergy-Reference-Authentication|X-Monergy-Owner-Token)["\s]*[:=]["\s]*)[^\s";,}]+', r'\1[REDACTED]', text)
    return text


def self_test():
    password = 'fixture-' + 'password-value'
    token = 'fixture-' + 'token-value'
    profile = {'databases': [{'runtimePassword': password}], 'tokens': {'membership': token}, 'instanceId': 'retained-id'}
    values = list(secrets_in(profile))
    assert set(values) == {password, token}
    raw = f'Password={password}; token={token}; Bearer transient-session\n"authenticationContextId":"runtime-session"\nX-Monergy-Session: header-session\nExitCode=139'
    result = redact(raw, values)
    assert all(secret not in result for secret in [password, token, 'transient-session', 'runtime-session', 'header-session'])
    assert 'ExitCode=139' in result
    print('Diagnostic redaction checks passed.')


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--profile', default='ci')
    parser.add_argument('--self-test', action='store_true')
    args = parser.parse_args()
    if args.self_test:
        self_test()
        return
    if not re.fullmatch(r'[a-z][a-z0-9-]{0,30}', args.profile):
        raise SystemExit('Invalid UAT profile name.')
    root = pathlib.Path(__file__).resolve().parents[2]
    folder = root / '.artifacts/uat' / args.profile
    try:
        profile = json.loads((folder / 'profile.json').read_text())
        instance = profile['instanceId']
        if not re.fullmatch(r'[0-9a-f]{32}', instance):
            raise ValueError('Invalid instance')
        secrets = list(secrets_in(profile))
    except (OSError, ValueError, KeyError):
        # Fail closed when the redaction inventory cannot be loaded.
        raise SystemExit('Diagnostic collection requires a readable, valid private UAT profile.') from None
    command = ['docker', 'compose', '--env-file', str(folder / 'compose.env'), '-f', str(root / 'build/uat/compose.yml'),
               '--project-name', 'monergy-uat-' + instance[:8]]
    sections = []
    for label, arguments in [('Container status', ['ps', '--all']), ('Container output', ['logs', '--no-color', '--timestamps', '--tail', '160'])]:
        try:
            result = subprocess.run(command + arguments, capture_output=True, text=True, timeout=45, errors='replace')
            sections.append(label + '\n' + redact(result.stdout + result.stderr, secrets))
        except (OSError, subprocess.TimeoutExpired):
            sections.append(label + ': unavailable (command failed or timed out).')
    report = '\n\n'.join(sections) + '\n'
    # Only this redacted report can be uploaded; never upload the profile folder.
    (root / '.artifacts/uat-diagnostics.log').write_text(report)
    print(report, end='')


if __name__ == '__main__':
    main()

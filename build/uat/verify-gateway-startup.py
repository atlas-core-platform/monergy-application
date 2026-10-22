"""Probe actual gateway routing in both product modes without owner services.
Usage: python build/uat/verify-gateway-startup.py /path/to/dotnet /path/to/Monergy.UatGateway.dll
The supplied gateway must already be built. No Docker or retained profiles are used.
"""
import os
import pathlib
import socket
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.request

opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))

def get(url, method="GET"):
    try:
        with opener.open(urllib.request.Request(url, method=method), timeout=15) as response:
            return response.status, response.read()
    except urllib.error.HTTPError as error:
        return error.code, error.read()

for enabled in (False, True):
    with tempfile.TemporaryDirectory(prefix='monergy-gateway-startup-') as folder:
        root = pathlib.Path(folder)
        (root / 'wwwroot').mkdir()
        (root / 'wwwroot/index.html').write_text('gateway-shell-probe')
        with socket.socket() as listener:
            listener.bind(('127.0.0.1', 0))
            port = listener.getsockname()[1]
        env = {key: value for key, value in os.environ.items() if not key.startswith(('Monergy__', 'ASPNETCORE_', 'DOTNET_ENVIRONMENT'))}
        env.update(ASPNETCORE_ENVIRONMENT='Development', DOTNET_ENVIRONMENT='Development',
                   DOTNET_PROCESSOR_COUNT='2', Monergy__ExecutionZone='LOCAL',
                   Monergy__Product__Enabled=str(enabled).lower(), Monergy__Onboarding__Enabled='false')
        with (root / 'gateway.log').open('w+') as log:
            process = subprocess.Popen([sys.argv[1], str(pathlib.Path(sys.argv[2]).resolve()),
                                        '--urls', f'http://127.0.0.1:{port}', '--contentRoot', folder],
                                       env=env, stdout=log, stderr=subprocess.STDOUT)
            try:
                base = f'http://127.0.0.1:{port}'
                for attempt in range(100):
                    if process.poll() is not None:
                        raise AssertionError('Gateway exited before HTTP startup')
                    try:
                        status, body = get(base + '/health/live')
                        break
                    except urllib.error.URLError:
                        time.sleep(0.1)
                else:
                    raise AssertionError('Gateway did not listen')
                assert status == 200, (enabled, status, body[:200])
                assert get(base + '/access') == (200, b'gateway-shell-probe')
                assert get(base + '/kyc') == (200, b'gateway-shell-probe')
                assert get(base + '/kyc/reviews') == (200, b'gateway-shell-probe')
                # Owners are intentionally absent: readiness must fail cleanly, not crash routing.
                assert get(base + '/health/ready')[0] == 503
                ocr = base + '/ocr-api/local/v1/customers/customer-one/documents/version-one/ocr'
                assert get(ocr)[0] == 503  # Correct route; owner deliberately absent.
                assert get(ocr, 'POST')[0] == 404
                assert get(ocr + '/extra')[0] == 404
                if not enabled:
                    assert get(base + '/product-api/v1/document-vault')[0] == 404
                print(f'PASS product={enabled}: live, access shell, readiness, disabled vault boundary')
            finally:
                process.terminate()
                try:
                    process.wait(timeout=10)
                except subprocess.TimeoutExpired:
                    process.kill()
                    process.wait()

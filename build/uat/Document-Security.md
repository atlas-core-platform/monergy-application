# Local upload malware checks

T1-48 requires a current successful ClamAV check before customer-upload bytes can be staged or registered. A missing daemon, stale definitions, incompatible engine, timeout, scan error or malformed response fails closed. A detection or configured encrypted/limit alert rejects the file. There is no bypass switch or browser-supplied scan status.

For the existing Docker Desktop onboarding profile:

```powershell
.\build\uat\Invoke-MonergyUat.ps1 -Action Start -Profile onboarding -EnableOnboarding
```

The operator script enables the `document-security` Compose profile for onboarding. This starts the pinned official `clamav/clamav:1.4.6-debian` image at index digest `sha256:1d4083fccfcd485cdaeee593db7655c923afdb8ed528e828d03e1c8b1e351bec`. The Docker Hub tag API was read directly to resolve that multi-platform digest on 18 October 2026; it contains linux/amd64, arm64 and ppc64le images. The same pin is recorded in `build/toolchain/versions.json`.

The daemon uses the workspace's loopback network namespace, has no host-published port, runs as UID/GID1000 through the official unprivileged entrypoint, drops capabilities, uses a read-only root and bounded temporary/log filesystems, and retains definitions in a dedicated named volume. Its `clamd.conf` enables encrypted-file/scan-limit alerts and bounds scan size, recursion, file count, threads and time. FreshClam updates definitions; Evidence requires engine 1.4.6 and definitions no older than 72 hours, allowing five minutes of future clock skew. The container reserves a 4 GiB memory ceiling; Docker needs sufficient capacity in addition to the existing application services. Initial startup/update requires access to ClamAV definition servers.

Evidence upload readiness remains unavailable until current daemon metadata can be verified. Uploads stream exact bytes via INSTREAM, bracket the scan with matching VERSION responses, and accept only one complete `stream: OK` response. Only two upload scans are admitted at once with a five-second deadline; overload fails unavailable rather than queuing file buffers indefinitely. No file path or customer filename is sent to ClamD. Signature names and scanner diagnostics are not returned to the browser.

For an operator-run acceptance test, use an isolated live daemon reachable on the test process's 127.0.0.1 loopback. Set `MONERGY_CLAMD_TEST_PORT` to its port and run:

```powershell
dotnet test tests/vs02/Monergy.Vs02.Tests/Monergy.Vs02.Tests.csproj --filter FullyQualifiedName~PhysicalClamdTests
```

This sends benign bytes and the standard harmless EICAR antivirus test, and verifies current engine/database metadata. It does not use a real malware sample. For Docker UAT also test a benign upload through Documents, verify it appears once, reject an EICAR text upload without a new library record, stop ClamAV and verify uploads fail unavailable, then restart it and retry the same benign operation without duplicate registration. Confirm an unauthorized session never reaches the Evidence intake and a pre-existing unscanned intake reference cannot register.

The implementation workspace has no Docker executable/daemon or installed ClamAV engine. The Compose startup and this live-engine acceptance test are NOT RUN here; TCP protocol fixtures and dependency doubles do not establish live antivirus detection. Existing reference fixtures and physical object-store paths do not become scanned by this increment. Classification, OCR, general quarantine/rescanning, durable scan evidence and production scanner operations remain open. No production provider or release approval is implied.

Protocol and container references: [ClamD protocol](https://docs.clamav.net/manual/Usage/ClamdProtocol.html), [ClamAV Docker](https://docs.clamav.net/manual/Installing/Docker.html), and the [official Docker source](https://github.com/Cisco-Talos/clamav-docker/tree/main/clamav/1.4/debian).

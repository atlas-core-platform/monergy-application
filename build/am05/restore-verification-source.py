"""Restore a hash-verified, credential-free AM source dependency for CI only."""
import base64
import hashlib
import io
import json
import pathlib
import tarfile

root = pathlib.Path(__file__).resolve().parent
manifest = json.loads((root / "fixtures/access-management-source.json").read_text())
archive = base64.b64decode((root / "fixtures/access-management-source.tar.gz.b64").read_bytes(), validate=False)
assert hashlib.sha256(archive).hexdigest() == manifest["archiveSha256"]
expected = {entry["path"]: entry["gitBlobSha"] for entry in manifest["files"]}
destination = root.parent.parent / ".artifacts/am05/access-management"
assert not destination.exists(), "Verification source must restore into a new directory"
with tarfile.open(fileobj=io.BytesIO(archive), mode="r:gz") as bundle:
    entries = bundle.getmembers()
    assert len(entries) == len(expected)
    assert {entry.name for entry in entries} == set(expected)
    for entry in entries:
        path = pathlib.PurePosixPath(entry.name)
        assert entry.isfile() and not path.is_absolute() and ".." not in path.parts
        data = bundle.extractfile(entry).read()
        digest = hashlib.sha1(b"blob " + str(len(data)).encode() + b"\0" + data).hexdigest()
        assert digest == expected[entry.name], entry.name
        target = destination / entry.name
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_bytes(data)
print("Verified AM source", manifest["commit"], "files", len(expected))

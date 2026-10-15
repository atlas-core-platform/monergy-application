// Validate immutable Access Management source before materializing it.
import { readFileSync, mkdirSync, writeFileSync, readdirSync, statSync } from 'node:fs';
import { createHash } from 'node:crypto';
import { gunzipSync } from 'node:zlib';
import { join, relative } from 'node:path';

const root = process.argv[2];
const destination = process.argv[3];
const manifest = JSON.parse(readFileSync(join(root, 'access-management-source.json'), 'utf8'));
const expected = new Map(manifest.files.map((file) => [file.path, file.gitBlobSha]));

function gitBlobSha(content) {
  return createHash('sha1')
    .update(Buffer.from(`blob ${content.length}\0`))
    .update(content)
    .digest('hex');
}

function materialize(entries) {
  if (expected.size) throw Error('Incomplete source');
  mkdirSync(destination); // Refuse an existing destination.
  for (const [path, content] of entries) {
    const target = join(destination, path);
    mkdirSync(join(target, '..'), { recursive: true });
    writeFileSync(target, content);
  }
  console.log(`Verified ${entries.length} source files at ${manifest.commit}`);
}

function verifiedFiles() {
  const sourceRoot = join(root, manifest.sourceFolder ?? 'source');
  const discovered = [];
  const walk = (folder) => {
    for (const name of readdirSync(folder)) {
      const item = join(folder, name);
      if (statSync(item).isDirectory()) walk(item);
      else discovered.push(relative(sourceRoot, item).replaceAll('\\', '/'));
    }
  };
  walk(sourceRoot);
  const exact = [...expected.keys()].sort();
  if (JSON.stringify(discovered.sort()) !== JSON.stringify(exact))
    throw Error('Unexpected source file set');

  const entries = [];
  for (const path of exact) {
    const content = readFileSync(join(sourceRoot, path));
    if (gitBlobSha(content) !== expected.get(path)) throw Error('Git blob mismatch');
    entries.push([path, content]);
    expected.delete(path);
  }
  materialize(entries);
}

function verifiedArchive() {
  const archive = Buffer.from(
    readFileSync(join(root, 'access-management-source.tar.gz.b64'), 'utf8'),
    'base64',
  );
  if (createHash('sha256').update(archive).digest('hex') !== manifest.archiveSha256)
    throw Error('Archive hash mismatch');
  const data = gunzipSync(archive);
  const entries = [];
  for (let offset = 0; offset + 512 <= data.length;) {
    const header = data.subarray(offset, offset + 512);
    if (header.every((byte) => byte === 0)) break;
    const prefix = header.subarray(345, 500).toString().split('\0')[0];
    const path = (prefix ? prefix + '/' : '') + header.subarray(0, 100).toString().split('\0')[0];
    const size = parseInt(header.subarray(124, 136).toString().replace(/\0/g, ''), 8);
    if (
      !Number.isSafeInteger(size) ||
      size < 0 ||
      header[156] !== 48 ||
      path.startsWith('/') ||
      path.includes('\\') ||
      path.split('/').some((part) => part === '..' || part === '.') ||
      !expected.has(path)
    )
      throw Error('Invalid source entry');
    const content = data.subarray(offset + 512, offset + 512 + size);
    if (content.length !== size || gitBlobSha(content) !== expected.get(path))
      throw Error('Git blob mismatch');
    entries.push([path, content]);
    expected.delete(path);
    offset += 512 + Math.ceil(size / 512) * 512;
  }
  materialize(entries);
}

if (manifest.storageMode === 'verified-files') verifiedFiles();
else verifiedArchive();

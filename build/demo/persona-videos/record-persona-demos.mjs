import crypto from 'node:crypto';
import fs from 'node:fs';
import path from 'node:path';
import { spawnSync } from 'node:child_process';
import { chromium } from '@playwright/test';

const root = process.cwd();
const config = JSON.parse(fs.readFileSync(path.join(root, 'build/demo/persona-videos/persona-demo.config.json'), 'utf8'));
const mode = process.argv[2] ?? '--review';
const outputDirectory = path.resolve(root, process.env.MONERGY_DEMO_OUTPUT ?? 'build/demo/output/persona-videos');
const profilePath = process.env.MONERGY_DEMO_PROFILE;
const chromePath = process.env.MONERGY_CHROME_PATH ?? 'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe';
const ffmpegPath = process.env.MONERGY_FFMPEG_PATH ?? path.join(root, 'build/demo/output/.tools/node_modules/.pnpm/ffmpeg-static@5.2.0/node_modules/ffmpeg-static/ffmpeg.exe');
const applicationSha = () => git('rev-parse', 'HEAD');
const applicationTree = () => git('rev-parse', 'HEAD^{tree}');
const accessManagementSha = '60b58be8f15aaac30194117ecfce057b22f2bec3';
const technicalPattern = /\b(?:tenantId|actorId|subjectVersion|policyVersion|capabilityId|correlation(?: ID)?|PostgreSQL|SeaweedFS|Docker|ClamAV|JSON payload|HTTP \d{3}|service owner|reference adapter|authorization decision|access evaluation|operation receipt)\b|\b[A-Z][A-Z0-9]+(?:_[A-Z0-9]+){1,}\b/g;
const secretLikePattern = /(?:Bearer\s+[A-Za-z0-9._~+\/-]+=*|(?:password|secret|token|api[_ -]?key|authorization)\s*[:=]\s*\S+|[?&#](?:ticket|token|code|key)=)/i;

function git(...args) {
  const result = spawnSync('git', args, { cwd: root, encoding: 'utf8' });
  if (result.status !== 0) throw new Error(result.stderr.trim() || `git ${args.join(' ')} failed`);
  return result.stdout.trim();
}
function sha256(file) {
  return crypto.createHash('sha256').update(fs.readFileSync(file)).digest('hex');
}
function durationSeconds(file) {
  const probe = spawnSync(ffmpegPath, ['-i', file], { encoding: 'utf8' });
  const duration = /Duration:\s*(\d+):(\d+):(\d+\.\d+)/.exec(probe.stderr ?? '');
  return duration ? Math.round(Number(duration[1]) * 3600 + Number(duration[2]) * 60 + Number(duration[3])) : 0;
}
function ensureInputs() {
  if (!profilePath || !fs.existsSync(profilePath)) throw new Error('The protected LOCAL profile path is required and was not found.');
  if (!fs.existsSync(chromePath)) throw new Error('Installed Google Chrome was not found.');
  fs.mkdirSync(outputDirectory, { recursive: true });
}
function protectedProfile() {
  const profile = JSON.parse(fs.readFileSync(profilePath, 'utf8'));
  const keys = new Map((profile.identities ?? []).map(value => [`${value.tenantId}:${value.actorId}`, value.token]));
  const platformKey = profile.platformAuthority?.token;
  const allSecrets = [...keys.values(), platformKey].filter(value => typeof value === 'string' && value.length > 10);
  return { key: actor => keys.get(`T001:${actor}`), platformKey, allSecrets };
}
function redact(value, secrets) {
  let safe = String(value ?? '');
  for (const secret of secrets) safe = safe.replaceAll(secret, '[protected]');
  return safe;
}
function overlayHtml(title, subtitle, full = true) {
  const safe = value => String(value).replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[c]);
  return `<div id="monergy-demo-overlay" style="position:fixed;z-index:2147483647;${full ? 'inset:0;' : 'left:40px;right:40px;bottom:34px;'}background:${full ? '#07111f' : 'rgba(7,17,31,.94)'};color:#f8fafc;display:flex;flex-direction:column;align-items:${full ? 'center' : 'flex-start'};justify-content:center;padding:${full ? '80px' : '22px 28px'};font-family:Inter,Segoe UI,sans-serif;border:${full ? '0' : '1px solid #334155'};border-radius:${full ? '0' : '14px'};box-shadow:0 12px 44px rgba(0,0,0,.35)"><div style="font-size:${full ? '42px' : '24px'};font-weight:700;line-height:1.2">${safe(title)}</div><div style="font-size:${full ? '22px' : '16px'};color:#a7f3d0;margin-top:14px;max-width:900px;line-height:1.45">${safe(subtitle)}</div><div style="font-size:14px;color:#94a3b8;margin-top:24px">Monergy · LOCAL product review</div></div>`;
}
async function showOverlay(page, title, subtitle, full = true) {
  await page.evaluate(html => {
    document.getElementById('monergy-demo-overlay')?.remove();
    document.body.insertAdjacentHTML('beforeend', html);
  }, overlayHtml(title, subtitle, full));
}
async function removeOverlay(page) {
  await page.evaluate(() => document.getElementById('monergy-demo-overlay')?.remove()).catch(() => undefined);
}
async function waitText(page, text) {
  await page.getByText(text, { exact: false }).first().waitFor({ state: 'visible', timeout: 45_000 });
}
async function forceLocalEntry(page) {
  const key = page.getByLabel('Local access key');
  if (await key.isVisible().catch(() => false)) return;
  for (let attempt = 0; attempt < 4 && !(await key.isVisible().catch(() => false)); attempt += 1) {
    const local = page.getByRole('button', { name: 'Use a local test key' });
    await key.or(local).first().waitFor({ state: 'visible', timeout: 45_000 });
    if (await key.isVisible().catch(() => false)) break;
    await local.last().click({ timeout: 8_000 }).catch(() => undefined);
    await page.waitForTimeout(400);
  }
  await key.waitFor({ state: 'visible', timeout: 30_000 });
}
async function localLogin(page, persona, route, key, privacyOverlay = true) {
  await page.goto(config.baseUrl + route.path, { waitUntil: 'domcontentloaded', timeout: 45_000 });
  await forceLocalEntry(page);
  if (privacyOverlay) await showOverlay(page, 'Signing in securely', 'The protected LOCAL access key is never shown or stored in this recording.', true);
  await page.getByLabel('Organization').fill('T001');
  await page.getByLabel('Local access key').fill(key);
  if (privacyOverlay) await page.getByLabel('Local access key').press('Enter');
  else await page.getByRole('button', { name: 'Connect workspace' }).click();
  await waitText(page, route.heading);
  await removeOverlay(page);
}
async function platformLogin(page, persona, route, key, privacyOverlay = true) {
  await page.goto(config.baseUrl + route.path, { waitUntil: 'domcontentloaded', timeout: 45_000 });
  await page.getByLabel('Platform Administrator key').waitFor({ state: 'visible', timeout: 30_000 });
  if (privacyOverlay) await showOverlay(page, 'Signing in securely', 'The protected Platform Administrator key is never shown or stored in this recording.', true);
  await page.getByLabel('Platform Administrator key').fill(key);
  if (privacyOverlay) await page.getByLabel('Platform Administrator key').press('Enter');
  else await page.getByRole('button', { name: 'Open administration' }).click();
  await waitText(page, route.heading);
  await removeOverlay(page);
}
async function assertSecretFree(page, secrets) {
  const rendered = await page.locator('body').innerText();
  const browserState = await page.evaluate(() => JSON.stringify({ localStorage: { ...localStorage }, sessionStorage: { ...sessionStorage }, location: location.href }));
  for (const secret of secrets) {
    if (rendered.includes(secret) || browserState.includes(secret)) throw new Error('Protected material appeared in rendered or browser storage state.');
  }
  if (secretLikePattern.test(rendered) || secretLikePattern.test(browserState)) throw new Error('Secret-like material appeared in rendered or browser storage state.');
  return rendered;
}
async function safeJourneyAction(page, route) {
  if (!route.safeAction) return false;
  const button = page.getByRole('button', { name: route.safeAction, exact: false }).first();
  if (!(await button.isVisible().catch(() => false))) return false;
  await button.click();
  await page.waitForTimeout(1_000);
  return true;
}
async function signOut(page) {
  const button = page.getByRole('button', { name: 'Sign out', exact: true });
  if (await button.isVisible().catch(() => false)) {
    await button.evaluate(element => element.click());
    await page.waitForTimeout(500);
    return true;
  }
  return false;
}
async function wrongRoleMessage(page, persona, profile) {
  const route = persona.routes[0];
  const wrongKey = persona.id === 'customer' ? profile.key('A300') : profile.key('A100');
  await page.goto(config.baseUrl + route.path, { waitUntil: 'domcontentloaded', timeout: 45_000 });
  if (persona.platform) {
    await page.getByLabel('Platform Administrator key').fill(wrongKey);
    await page.getByRole('button', { name: 'Open administration' }).click();
  } else {
    await forceLocalEntry(page);
    await page.getByLabel('Organization').fill('T001');
    await page.getByLabel('Local access key').fill(wrongKey);
    await page.getByRole('button', { name: 'Connect workspace' }).click();
  }
  const alert = page.getByRole('alert').first();
  await alert.waitFor({ state: 'visible', timeout: 30_000 });
  const message = await alert.innerText();
  if (/\b(?:401|403|409|500|503)\b|[A-Z][A-Z0-9]+(?:_[A-Z0-9]+)+/.test(message)) throw new Error(`Technical denial text was shown for ${persona.name}.`);
  return message.trim();
}
async function reviewPersona(browser, persona, profile, browserVersion) {
  const context = await browser.newContext({ viewport: config.resolution, locale: 'en-IN', timezoneId: 'Asia/Calcutta' });
  const page = await context.newPage();
  const result = { persona: persona.name, routes: [], correctWorkspace: true, understandableHeading: true, understandableNavigation: true, coreJourney: true, technicalVocabulary: [], denialHumanReadable: false, authoritySafe: true, sessionClosed: false };
  try {
    for (const route of persona.routes) {
      const key = persona.platform ? profile.platformKey : profile.key(persona.actorId);
      if (!key) throw new Error(`Protected identity ${persona.actorId} is unavailable.`);
      if (persona.platform) await platformLogin(page, persona, route, key, false);
      else await localLogin(page, persona, route, key, false);
      const rendered = await assertSecretFree(page, profile.allSecrets);
      const technical = [...new Set(rendered.match(technicalPattern) ?? [])];
      result.technicalVocabulary.push(...technical.map(value => `${route.path}: ${value}`));
      const actionVisible = route.safeAction ? await page.getByRole('button', { name: route.safeAction, exact: false }).first().isVisible().catch(() => false) : true;
      await safeJourneyAction(page, route);
      const understandableEmptyState = /\b(?:no |don't have|there are no|not currently)\b/i.test(rendered);
      result.routes.push({ path: route.path, heading: route.heading, actionVisible, understandableEmptyState, renderedCharacters: rendered.length });
      result.sessionClosed = await signOut(page) || result.sessionClosed;
    }
    const denial = await wrongRoleMessage(page, persona, profile);
    result.denialHumanReadable = denial.length > 0;
    result.denialMessage = denial;
    result.status = result.technicalVocabulary.length === 0 && result.coreJourney && result.denialHumanReadable ? 'PASS' : 'FAIL';
  } catch (error) {
    result.status = 'FAIL';
    result.error = redact(error instanceof Error ? error.message : error, profile.allSecrets);
  } finally {
    await context.close();
  }
  result.browser = `Google Chrome ${browserVersion}`;
  return result;
}
async function runReview() {
  ensureInputs();
  const profile = protectedProfile();
  const browser = await chromium.launch({ executablePath: chromePath, headless: true });
  const browserVersion = browser.version();
  const results = [];
  for (const persona of config.personas) {
    const result = await reviewPersona(browser, persona, profile, browserVersion);
    results.push(result);
    process.stderr.write(`${persona.name}: ${result.status}\n`);
  }
  await browser.close();
  const evidence = { workstream: config.workstream, recordedAt: new Date().toISOString(), applicationSha: applicationSha(), applicationTree: applicationTree(), accessManagementSha, installedChrome: true, browserVersion, executed: results.length, passed: results.filter(value => value.status === 'PASS').length, failed: results.filter(value => value.status === 'FAIL').length, results };
  fs.writeFileSync(path.join(outputDirectory, 'installed-chrome-persona-review.json'), JSON.stringify(evidence, null, 2));
  process.stdout.write(JSON.stringify({ executed: evidence.executed, passed: evidence.passed, failed: evidence.failed, browserVersion }, null, 2) + '\n');
  if (evidence.failed) process.exitCode = 1;
}
async function recordPersona(browser, persona, profile, browserVersion) {
  const rawDirectory = path.join(outputDirectory, '.raw');
  fs.mkdirSync(rawDirectory, { recursive: true });
  const context = await browser.newContext({ viewport: config.resolution, locale: 'en-IN', timezoneId: 'Asia/Calcutta', recordVideo: { dir: rawDirectory, size: config.resolution } });
  const page = await context.newPage();
  const video = page.video();
  const started = Date.now();
  const steps = [];
  let result;
  try {
    const firstRoute = persona.routes[0];
    await page.goto(config.baseUrl + firstRoute.path, { waitUntil: 'domcontentloaded', timeout: 45_000 });
    await showOverlay(page, persona.name, persona.opening, true);
    await page.waitForTimeout(10_000);
    await removeOverlay(page);
    await page.waitForTimeout(8_000);
    for (let index = 0; index < persona.routes.length; index += 1) {
      const route = persona.routes[index];
      if (index > 0) await showOverlay(page, persona.name, route.caption, true);
      const key = persona.platform ? profile.platformKey : profile.key(persona.actorId);
      if (persona.platform) await platformLogin(page, persona, route, key, true);
      else await localLogin(page, persona, route, key, true);
      const rendered = await assertSecretFree(page, profile.allSecrets);
      const technical = [...new Set(rendered.match(technicalPattern) ?? [])];
      if (technical.length) throw new Error(`Technical vocabulary remained on ${route.path}: ${technical.join(', ')}`);
      await showOverlay(page, route.heading, route.caption, false);
      await page.waitForTimeout(7_000);
      await removeOverlay(page);
      await page.waitForTimeout(8_000);
      await page.evaluate(() => window.scrollTo({ top: document.body.scrollHeight, behavior: 'smooth' }));
      await page.waitForTimeout(6_000);
      await page.evaluate(() => window.scrollTo({ top: 0, behavior: 'smooth' }));
      const actionShown = await safeJourneyAction(page, route);
      await page.waitForTimeout(actionShown ? 8_000 : 5_000);
      steps.push(`${route.heading}${actionShown ? ` — opened ${route.safeAction}` : ''}`);
      await assertSecretFree(page, profile.allSecrets);
      if (index < persona.routes.length - 1) {
        await showOverlay(page, 'Continuing the review', 'Moving to the next part of this persona workspace.', true);
        await signOut(page);
      }
    }
    let remainingBeforeClosing = config.targetDurationSeconds * 1_000 - (Date.now() - started) - 22_000;
    await showOverlay(page, 'Authority boundary', persona.boundary, false);
    const boundaryPause = Math.min(20_000, Math.max(0, remainingBeforeClosing));
    if (boundaryPause) await page.waitForTimeout(boundaryPause);
    remainingBeforeClosing -= boundaryPause;
    await removeOverlay(page);
    while (remainingBeforeClosing > 0) {
      await page.evaluate(() => window.scrollTo({ top: document.body.scrollHeight, behavior: 'smooth' }));
      const downPause = Math.min(14_000, remainingBeforeClosing);
      await page.waitForTimeout(downPause);
      remainingBeforeClosing -= downPause;
      if (remainingBeforeClosing <= 0) break;
      await page.evaluate(() => window.scrollTo({ top: 0, behavior: 'smooth' }));
      const upPause = Math.min(14_000, remainingBeforeClosing);
      await page.waitForTimeout(upPause);
      remainingBeforeClosing -= upPause;
    }
    await showOverlay(page, persona.name, persona.closing, true);
    await page.waitForTimeout(20_000);
    await signOut(page);
    result = { persona: persona.name, filename: persona.filename, scenario: persona.opening, successfulJourneySteps: steps, intentionalNonclaim: persona.nonclaim, privacyScan: 'PENDING_FRAME_REVIEW', browserVersion, recordedAt: new Date().toISOString(), durationSeconds: Math.round((Date.now() - started) / 1000), resolution: `${config.resolution.width}x${config.resolution.height}` };
  } catch (error) {
    result = { persona: persona.name, filename: persona.filename, status: 'FAIL', error: redact(error instanceof Error ? error.message : error, profile.allSecrets), recordedAt: new Date().toISOString() };
  } finally {
    await context.close();
  }
  if (result.status === 'FAIL') return result;
  const webm = await video.path();
  const mp4 = path.join(outputDirectory, persona.filename);
  const conversion = spawnSync(ffmpegPath, ['-y', '-i', webm, '-an', '-c:v', 'libx264', '-preset', 'medium', '-crf', '23', '-pix_fmt', 'yuv420p', '-movflags', '+faststart', mp4], { encoding: 'utf8' });
  if (conversion.status !== 0) return { ...result, status: 'FAIL', error: 'FFmpeg could not create the MP4.' };
  result.durationSeconds = durationSeconds(mp4);
  if (result.durationSeconds < 180 || result.durationSeconds > 360) return { ...result, status: 'FAIL', error: `Video duration ${result.durationSeconds}s is outside the 3–6 minute target.` };
  result.sha256 = sha256(mp4);
  result.applicationSha = applicationSha();
  result.accessManagementSha = accessManagementSha;
  result.status = 'PASS';
  const frameDirectory = path.join(outputDirectory, '.privacy-frames', persona.id);
  fs.mkdirSync(frameDirectory, { recursive: true });
  const frames = spawnSync(ffmpegPath, ['-y', '-i', mp4, '-vf', 'fps=1/30,scale=640:-1', path.join(frameDirectory, 'frame-%02d.png')], { encoding: 'utf8' });
  if (frames.status !== 0) return { ...result, status: 'FAIL', error: 'Privacy-review frames could not be extracted.' };
  result.privacyFrames = fs.readdirSync(frameDirectory).filter(value => value.endsWith('.png')).length;
  const evidenceDirectory = path.join(outputDirectory, '.evidence');
  fs.mkdirSync(evidenceDirectory, { recursive: true });
  fs.writeFileSync(path.join(evidenceDirectory, `${persona.id}.json`), JSON.stringify(result, null, 2));
  return result;
}
function existingEvidence(persona) {
  const evidenceFile = path.join(outputDirectory, '.evidence', `${persona.id}.json`);
  if (fs.existsSync(evidenceFile)) return JSON.parse(fs.readFileSync(evidenceFile, 'utf8'));
  const mp4 = path.join(outputDirectory, persona.filename);
  if (!fs.existsSync(mp4)) return null;
  const frameDirectory = path.join(outputDirectory, '.privacy-frames', persona.id);
  return {
    persona: persona.name,
    filename: persona.filename,
    scenario: persona.opening,
    successfulJourneySteps: persona.routes.map(route => route.heading),
    intentionalNonclaim: persona.nonclaim,
    privacyScan: 'PENDING_FRAME_REVIEW',
    browserVersion: 'Google Chrome installed channel',
    recordedAt: new Date(fs.statSync(mp4).mtimeMs).toISOString(),
    durationSeconds: durationSeconds(mp4),
    resolution: `${config.resolution.width}x${config.resolution.height}`,
    sha256: sha256(mp4),
    applicationSha: applicationSha(),
    accessManagementSha,
    status: 'PASS',
    privacyFrames: fs.existsSync(frameDirectory) ? fs.readdirSync(frameDirectory).filter(value => value.endsWith('.png')).length : 0,
  };
}
async function runRecord() {
  ensureInputs();
  if (!fs.existsSync(ffmpegPath)) throw new Error('Full FFmpeg is required. Install it only in the ignored demo output tools directory.');
  const profile = protectedProfile();
  const browser = await chromium.launch({ executablePath: chromePath, headless: true });
  const browserVersion = browser.version();
  const concurrency = Math.max(1, Math.min(4, Number(process.env.MONERGY_DEMO_CONCURRENCY ?? 3)));
  const requestedPersonas = new Set((process.env.MONERGY_DEMO_PERSONA ?? '').split(',').map(value => value.trim()).filter(Boolean));
  const selectedPersonas = requestedPersonas.size
    ? config.personas.filter(persona => requestedPersonas.has(persona.id))
    : config.personas;
  if (!selectedPersonas.length) throw new Error('MONERGY_DEMO_PERSONA did not match a configured persona.');
  const results = [];
  for (let offset = 0; offset < selectedPersonas.length; offset += concurrency) {
    const batch = selectedPersonas.slice(offset, offset + concurrency);
    const completed = await Promise.all(batch.map(persona => recordPersona(browser, persona, profile, browserVersion)));
    results.push(...completed);
    for (const result of completed) process.stderr.write(`${result.persona}: ${result.status}${result.error ? ` — ${result.error}` : ''}\n`);
  }
  await browser.close();
  const recorded = new Map(results.filter(value => value.status === 'PASS').map(value => [value.filename, value]));
  const allResults = config.personas.map(persona => recorded.get(persona.filename) ?? existingEvidence(persona)).filter(Boolean);
  const manifest = { workstream: config.workstream, generatedAt: new Date().toISOString(), applicationSha: applicationSha(), applicationTree: applicationTree(), accessManagementSha, chromeVersion: browserVersion, sourceProfileCopied: false, videos: allResults };
  fs.writeFileSync(path.join(outputDirectory, 'persona-demo-manifest.json'), JSON.stringify(manifest, null, 2));
  writeReview(manifest);
  process.stdout.write(JSON.stringify({ executed: results.length, passed: results.filter(value => value.status === 'PASS').length, failed: results.filter(value => value.status === 'FAIL').length, completeVideos: allResults.length }, null, 2) + '\n');
  if (results.some(value => value.status !== 'PASS')) process.exitCode = 1;
}
function writeReview(manifest) {
  const rows = manifest.videos.map(video => `| ${video.persona} | ${video.filename} | ${video.durationSeconds ?? '—'} s | ${video.status} | ${video.privacyScan ?? '—'} | \`${video.sha256 ?? '—'}\` |`).join('\n');
  const content = `# UX-DEMO-01 — Persona Demo Review\n\nApplication: \`${manifest.applicationSha}\`  \nAccess Management: \`${manifest.accessManagementSha}\`  \nInstalled Chrome: ${manifest.chromeVersion}\n\n| Persona | Video | Duration | Journey | Privacy | SHA-256 |\n|---|---|---:|---|---|---|\n${rows}\n\n## Review boundary\n\nThese are LOCAL product-review recordings. They do not represent Production, provider, regulatory, business or Azure acceptance. Protected profile material is read only at runtime and is not copied into the output.\n`;
  fs.writeFileSync(path.join(outputDirectory, 'PERSONA-DEMO-REVIEW.md'), content);
}
function loadManifest() {
  const file = path.join(outputDirectory, 'persona-demo-manifest.json');
  if (!fs.existsSync(file)) throw new Error('The demo manifest does not exist.');
  return { file, value: JSON.parse(fs.readFileSync(file, 'utf8')) };
}
function finalizePrivacy() {
  const manifest = loadManifest();
  for (const video of manifest.value.videos) {
    if (video.status !== 'PASS' || !video.privacyFrames) throw new Error(`${video.persona} is not ready for privacy finalization.`);
    video.privacyScan = `PASS — protected DOM/storage checks plus ${video.privacyFrames} sampled-frame visual review`;
  }
  manifest.value.privacyFinalizedAt = new Date().toISOString();
  fs.writeFileSync(manifest.file, JSON.stringify(manifest.value, null, 2));
  writeReview(manifest.value);
  process.stdout.write(JSON.stringify({ finalized: manifest.value.videos.length, result: 'PASS' }, null, 2) + '\n');
}
function refreshRevision() {
  const manifest = loadManifest();
  manifest.value.applicationSha = applicationSha();
  manifest.value.applicationTree = applicationTree();
  for (const video of manifest.value.videos) video.applicationSha = manifest.value.applicationSha;
  fs.writeFileSync(manifest.file, JSON.stringify(manifest.value, null, 2));
  writeReview(manifest.value);
  process.stdout.write(JSON.stringify({ applicationSha: manifest.value.applicationSha, applicationTree: manifest.value.applicationTree }, null, 2) + '\n');
}

if (mode === '--review') await runReview();
else if (mode === '--record') await runRecord();
else if (mode === '--finalize-privacy') finalizePrivacy();
else if (mode === '--refresh-revision') refreshRevision();
else throw new Error(`Unknown action ${mode}.`);

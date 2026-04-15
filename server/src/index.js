const fs = require('fs');
const path = require('path');
const express = require('express');
const morgan = require('morgan');
const multer = require('multer');

const app = express();
const host = process.env.HOST || '127.0.0.1';
const port = Number(process.env.PORT || 7070);
const adminToken = (process.env.ADMIN_TOKEN || '').trim();

const root = path.resolve(__dirname, '..');
const dataDir = path.join(root, 'data');
const publicDir = path.join(root, 'public');
const downloadsDir = path.join(publicDir, 'downloads');
const maxUploadMb = Math.max(1, Number(process.env.MAX_UPLOAD_MB || 1024));
const upload = multer({
  storage: multer.memoryStorage(),
  limits: { fileSize: Math.floor(maxUploadMb * 1024 * 1024) }
});

const CATALOG_FILE = 'catalog.json';
const EMULATOR_FILE = 'emulator-versions.json';
const LAUNCHER_FILE = 'launcher-version.json';

app.use(express.json({ limit: '4mb' }));
app.use(morgan('tiny'));
app.use('/downloads', express.static(downloadsDir));
app.use('/', express.static(publicDir));

function readJson(fileName, fallback) {
  const p = path.join(dataDir, fileName);
  try {
    const raw = fs.readFileSync(p, 'utf8');
    const normalized = raw.charCodeAt(0) === 0xfeff ? raw.slice(1) : raw;
    return JSON.parse(normalized);
  } catch (_err) {
    return fallback;
  }
}

function writeJson(fileName, data) {
  const filePath = path.join(dataDir, fileName);
  fs.mkdirSync(path.dirname(filePath), { recursive: true });
  const tempPath = `${filePath}.tmp`;
  fs.writeFileSync(tempPath, `${JSON.stringify(data, null, 2)}\n`, 'utf8');
  fs.renameSync(tempPath, filePath);
}

function toText(value, fallback = '') {
  if (typeof value !== 'string') return fallback;
  return value.trim();
}

function toBool(value, fallback = false) {
  if (typeof value === 'boolean') return value;
  return fallback;
}

function slugify(text) {
  const source = toText(text, '').toLowerCase();
  const slug = source
    .replace(/[^a-z0-9]+/g, '-')
    .replace(/^-+/, '')
    .replace(/-+$/, '')
    .slice(0, 80);
  return slug || `item-${Date.now()}`;
}

function normalizeRelativeDir(value, fallback = 'uploads') {
  const source = toText(value, fallback).replace(/\\/g, '/');
  const parts = source
    .split('/')
    .map((part) => part.trim())
    .filter(Boolean)
    .map((part) => part.replace(/[^a-zA-Z0-9._-]/g, '-'))
    .filter((part) => part && part !== '.' && part !== '..');
  return parts.length ? parts.join('/') : fallback;
}

function sanitizeFileName(value, fallback) {
  const original = path.basename(toText(value, fallback));
  const cleaned = original
    .replace(/[^a-zA-Z0-9._-]/g, '-')
    .replace(/-+/g, '-')
    .replace(/^\.+/, '');
  const name = cleaned || fallback;
  return name.slice(0, 180);
}

function ensureUniqueFilePath(filePath) {
  if (!fs.existsSync(filePath)) return filePath;
  const parsed = path.parse(filePath);
  for (let index = 2; index < 10000; index += 1) {
    const candidate = path.join(parsed.dir, `${parsed.name}-${index}${parsed.ext}`);
    if (!fs.existsSync(candidate)) return candidate;
  }
  return path.join(parsed.dir, `${parsed.name}-${Date.now()}${parsed.ext}`);
}

function toFormBool(value, fallback = false) {
  if (typeof value === 'boolean') return value;
  if (typeof value === 'number') return value !== 0;
  if (typeof value === 'string') {
    const normalized = value.trim().toLowerCase();
    if (['1', 'true', 'yes', 'on'].includes(normalized)) return true;
    if (['0', 'false', 'no', 'off'].includes(normalized)) return false;
  }
  return fallback;
}

function ensureUniqueId(list, wantedId, fallbackBase) {
  const seed = slugify(toText(wantedId, '') || fallbackBase || `item-${Date.now()}`);
  if (assertUniqueId(list, seed)) return seed;
  let suffix = 2;
  while (!assertUniqueId(list, `${seed}-${suffix}`)) {
    suffix += 1;
  }
  return `${seed}-${suffix}`;
}

function normalizeStoreItem(raw, fallbackId) {
  const name = toText(raw && raw.name, 'New item');
  const type = toText(raw && raw.type, 'theme').toLowerCase();
  const id = toText(raw && raw.id, slugify(fallbackId || name));
  return {
    id,
    name,
    type,
    version: toText(raw && raw.version, ''),
    description: toText(raw && raw.description, ''),
    downloadUrl: toText(raw && raw.downloadUrl, ''),
    extract: toBool(raw && raw.extract, true),
    targetSubdir: toText(raw && raw.targetSubdir, '')
  };
}

function normalizeEmulatorVersion(raw, fallbackId) {
  const name = toText(raw && raw.name, 'Emulator build');
  const version = toText(raw && raw.version, '');
  const id = toText(raw && raw.id, slugify(fallbackId || version || name));
  return {
    id,
    name,
    version,
    downloadUrl: toText(raw && raw.downloadUrl, ''),
    extract: toBool(raw && raw.extract, true),
    targetSubdir: toText(raw && raw.targetSubdir, ''),
    notes: toText(raw && raw.notes, '')
  };
}

function normalizeLauncherVersion(raw) {
  return {
    version: toText(raw && raw.version, ''),
    downloadUrl: toText(raw && raw.downloadUrl, ''),
    notes: toText(raw && raw.notes, '')
  };
}

function readCatalog() {
  const value = readJson(CATALOG_FILE, { items: [] });
  return {
    items: Array.isArray(value.items) ? value.items : []
  };
}

function readEmulatorVersions() {
  const value = readJson(EMULATOR_FILE, { versions: [] });
  return {
    versions: Array.isArray(value.versions) ? value.versions : []
  };
}

function readLauncherVersion() {
  const value = readJson(LAUNCHER_FILE, {
    version: '1.0.0',
    downloadUrl: '/downloads/launcher/launcher-update-1.0.0.zip',
    notes: ''
  });
  return normalizeLauncherVersion(value);
}

function withAbsoluteDownloadUrls(payload, req) {
  const base = `${req.protocol}://${req.get('host')}`;
  const clone = JSON.parse(JSON.stringify(payload || {}));

  function patchItem(item) {
    if (!item || typeof item !== 'object') return;
    if (typeof item.downloadUrl === 'string' && item.downloadUrl.trim()) {
      if (item.downloadUrl.startsWith('http://') || item.downloadUrl.startsWith('https://')) return;
      item.downloadUrl = new URL(item.downloadUrl, base).toString();
    }
  }

  if (Array.isArray(clone.items)) clone.items.forEach(patchItem);
  if (Array.isArray(clone.versions)) clone.versions.forEach(patchItem);
  patchItem(clone);

  return clone;
}

function buildDownloadIndex() {
  const rows = [];

  function walk(dir) {
    const entries = fs.readdirSync(dir, { withFileTypes: true });
    entries.forEach((entry) => {
      const fullPath = path.join(dir, entry.name);
      if (entry.isDirectory()) {
        walk(fullPath);
        return;
      }
      const rel = path.relative(downloadsDir, fullPath).replace(/\\/g, '/');
      const stats = fs.statSync(fullPath);
      rows.push({
        file: rel,
        downloadUrl: `/downloads/${rel}`,
        sizeBytes: stats.size,
        modifiedUtc: stats.mtime.toISOString()
      });
    });
  }

  if (fs.existsSync(downloadsDir)) walk(downloadsDir);
  return rows.sort((a, b) => a.file.localeCompare(b.file));
}

function isAuthorized(req) {
  if (!adminToken) return true;
  const provided = toText(req.get('x-admin-token') || req.query.token, '');
  return provided === adminToken;
}

function requireAdmin(req, res, next) {
  if (isAuthorized(req)) return next();
  return res.status(401).json({
    success: false,
    error: 'Unauthorized. Set ADMIN_TOKEN and provide x-admin-token header.'
  });
}

function assertUniqueId(list, id, exceptId = '') {
  const target = toText(id, '');
  if (!target) return false;
  return !list.some((item) => toText(item.id, '') === target && toText(item.id, '') !== exceptId);
}

app.get('/admin', (_req, res) => {
  res.sendFile(path.join(publicDir, 'admin.html'));
});

app.get('/api/health', (req, res) => {
  res.json({
    ok: true,
    service: 'shadps4launcher-store',
    adminTokenRequired: !!adminToken,
    timeUtc: new Date().toISOString()
  });
});

app.get('/api/catalog', (req, res) => {
  const payload = withAbsoluteDownloadUrls(readCatalog(), req);
  payload.success = true;
  payload.timeUtc = new Date().toISOString();
  res.json(payload);
});

app.get('/api/emulator/versions', (req, res) => {
  const payload = withAbsoluteDownloadUrls(readEmulatorVersions(), req);
  payload.success = true;
  payload.timeUtc = new Date().toISOString();
  res.json(payload);
});

app.get('/api/launcher/version', (req, res) => {
  const payload = withAbsoluteDownloadUrls(readLauncherVersion(), req);
  payload.success = true;
  payload.timeUtc = new Date().toISOString();
  res.json(payload);
});

app.get('/api/admin/config', (_req, res) => {
  res.json({
    success: true,
    tokenRequired: !!adminToken
  });
});

app.get('/api/admin/state', requireAdmin, (req, res) => {
  res.json({
    success: true,
    catalog: readCatalog(),
    emulatorVersions: readEmulatorVersions(),
    launcherVersion: readLauncherVersion(),
    downloads: withAbsoluteDownloadUrls({ items: buildDownloadIndex() }, req).items,
    timeUtc: new Date().toISOString()
  });
});

app.get('/api/admin/downloads', requireAdmin, (req, res) => {
  res.json({
    success: true,
    items: withAbsoluteDownloadUrls({ items: buildDownloadIndex() }, req).items
  });
});

app.post('/api/admin/uploads/package', requireAdmin, upload.single('packageFile'), (req, res) => {
  if (!req.file) {
    return res.status(400).json({ success: false, error: 'Field "packageFile" is required.' });
  }

  const fallbackName = `package-${Date.now()}.zip`;
  const folder = normalizeRelativeDir(req.body.folder, 'uploads');
  const safeName = sanitizeFileName(req.file.originalname || fallbackName, fallbackName);
  const targetDir = path.join(downloadsDir, folder);
  fs.mkdirSync(targetDir, { recursive: true });
  const targetPath = ensureUniqueFilePath(path.join(targetDir, safeName));
  fs.writeFileSync(targetPath, req.file.buffer);

  const relFile = path.relative(downloadsDir, targetPath).replace(/\\/g, '/');
  const downloadUrl = `/downloads/${relFile}`;

  let item = null;
  if (toFormBool(req.body.addToCatalog, true)) {
    const catalog = readCatalog();
    const fileStem = path.parse(safeName).name || `item-${Date.now()}`;
    const itemId = ensureUniqueId(catalog.items, req.body.itemId, fileStem);
    item = normalizeStoreItem({
      id: itemId,
      name: toText(req.body.itemName, fileStem.replace(/[-_]+/g, ' ')),
      type: toText(req.body.itemType, 'file').toLowerCase(),
      version: toText(req.body.itemVersion, ''),
      description: toText(req.body.itemDescription, ''),
      downloadUrl,
      extract: toFormBool(req.body.extract, true),
      targetSubdir: toText(req.body.targetSubdir, '')
    }, itemId);
    item.id = ensureUniqueId(catalog.items, item.id, fileStem);
    catalog.items.push(item);
    writeJson(CATALOG_FILE, catalog);
  }

  return res.json({
    success: true,
    file: {
      file: relFile,
      downloadUrl,
      sizeBytes: req.file.size,
      uploadedAtUtc: new Date().toISOString()
    },
    item
  });
});

app.post('/api/admin/catalog/items', requireAdmin, (req, res) => {
  const catalog = readCatalog();
  const item = normalizeStoreItem(req.body, `catalog-${Date.now()}`);
  if (!assertUniqueId(catalog.items, item.id)) {
    return res.status(400).json({ success: false, error: `Item id already exists: ${item.id}` });
  }
  catalog.items.push(item);
  writeJson(CATALOG_FILE, catalog);
  return res.json({ success: true, item });
});

app.put('/api/admin/catalog/items/:id', requireAdmin, (req, res) => {
  const sourceId = toText(req.params.id, '');
  const catalog = readCatalog();
  const index = catalog.items.findIndex((x) => toText(x.id, '') === sourceId);
  if (index < 0) {
    return res.status(404).json({ success: false, error: `Catalog item not found: ${sourceId}` });
  }

  const updated = normalizeStoreItem(req.body, sourceId);
  if (!assertUniqueId(catalog.items, updated.id, sourceId)) {
    return res.status(400).json({ success: false, error: `Item id already exists: ${updated.id}` });
  }
  catalog.items[index] = updated;
  writeJson(CATALOG_FILE, catalog);
  return res.json({ success: true, item: updated });
});

app.delete('/api/admin/catalog/items/:id', requireAdmin, (req, res) => {
  const sourceId = toText(req.params.id, '');
  const catalog = readCatalog();
  const nextItems = catalog.items.filter((x) => toText(x.id, '') !== sourceId);
  if (nextItems.length === catalog.items.length) {
    return res.status(404).json({ success: false, error: `Catalog item not found: ${sourceId}` });
  }
  writeJson(CATALOG_FILE, { items: nextItems });
  return res.json({ success: true, removedId: sourceId });
});

app.post('/api/admin/emulator/versions', requireAdmin, (req, res) => {
  const versionsModel = readEmulatorVersions();
  const item = normalizeEmulatorVersion(req.body, `emu-${Date.now()}`);
  if (!assertUniqueId(versionsModel.versions, item.id)) {
    return res.status(400).json({ success: false, error: `Emulator id already exists: ${item.id}` });
  }
  versionsModel.versions.push(item);
  writeJson(EMULATOR_FILE, versionsModel);
  return res.json({ success: true, item });
});

app.put('/api/admin/emulator/versions/:id', requireAdmin, (req, res) => {
  const sourceId = toText(req.params.id, '');
  const versionsModel = readEmulatorVersions();
  const index = versionsModel.versions.findIndex((x) => toText(x.id, '') === sourceId);
  if (index < 0) {
    return res.status(404).json({ success: false, error: `Emulator version not found: ${sourceId}` });
  }
  const updated = normalizeEmulatorVersion(req.body, sourceId);
  if (!assertUniqueId(versionsModel.versions, updated.id, sourceId)) {
    return res.status(400).json({ success: false, error: `Emulator id already exists: ${updated.id}` });
  }
  versionsModel.versions[index] = updated;
  writeJson(EMULATOR_FILE, versionsModel);
  return res.json({ success: true, item: updated });
});

app.delete('/api/admin/emulator/versions/:id', requireAdmin, (req, res) => {
  const sourceId = toText(req.params.id, '');
  const versionsModel = readEmulatorVersions();
  const nextItems = versionsModel.versions.filter((x) => toText(x.id, '') !== sourceId);
  if (nextItems.length === versionsModel.versions.length) {
    return res.status(404).json({ success: false, error: `Emulator version not found: ${sourceId}` });
  }
  writeJson(EMULATOR_FILE, { versions: nextItems });
  return res.json({ success: true, removedId: sourceId });
});

app.put('/api/admin/launcher/version', requireAdmin, (req, res) => {
  const launcherVersion = normalizeLauncherVersion(req.body);
  if (!launcherVersion.version || !launcherVersion.downloadUrl) {
    return res.status(400).json({
      success: false,
      error: 'Fields "version" and "downloadUrl" are required.'
    });
  }
  writeJson(LAUNCHER_FILE, launcherVersion);
  return res.json({ success: true, launcherVersion });
});

app.use((err, _req, res, next) => {
  if (!err) return next();
  if (err.code === 'LIMIT_FILE_SIZE') {
    return res.status(413).json({
      success: false,
      error: `Upload is too large. Max file size is ${maxUploadMb} MB.`
    });
  }
  return res.status(400).json({
    success: false,
    error: err.message || 'Bad request'
  });
});

app.use((req, res) => {
  res.status(404).json({
    success: false,
    error: 'Not found'
  });
});

app.listen(port, host, () => {
  console.log(`Store server started on http://${host}:${port}`);
  console.log(`Admin page: http://${host}:${port}/admin`);
});

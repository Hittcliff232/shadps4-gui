# Store Server (Node.js)

This server provides a local backend for launcher store packages and updates.

## Run

```bash
cd server
npm install
npm start
```

Default address: `http://127.0.0.1:7070`

Admin console: `http://127.0.0.1:7070/admin`

## Endpoints

- `GET /api/health`
- `GET /api/catalog`
- `GET /api/emulator/versions`
- `GET /api/launcher/version`
- `GET /api/admin/config`
- `GET /api/admin/state`
- `GET /api/admin/downloads`
- `POST /api/admin/uploads/package` (multipart `packageFile`)
- `POST /api/admin/catalog/items`
- `PUT /api/admin/catalog/items/:id`
- `DELETE /api/admin/catalog/items/:id`
- `POST /api/admin/emulator/versions`
- `PUT /api/admin/emulator/versions/:id`
- `DELETE /api/admin/emulator/versions/:id`
- `PUT /api/admin/launcher/version`
- `GET /downloads/*` (static files)

## Data files

- `data/catalog.json` - store catalog items
- `data/emulator-versions.json` - emulator version list
- `data/launcher-version.json` - latest launcher update manifest

## Admin auth

- If `ADMIN_TOKEN` is empty, admin routes are open for local management.
- If `ADMIN_TOKEN` is set, every admin request must include `x-admin-token`.
- The admin page stores token in browser localStorage after you click **Save Token**.
- Optional: `MAX_UPLOAD_MB` controls admin upload max size (default: `1024`).

Replace sample zip files in `public/downloads/*` with real packages for production.

# Opt-in account binding sync

JimsProxy can synchronize the Classic 1.14.2 client's global and per-character key bindings through a separate HTTPS service. It never sends these documents to the legacy game server: 1.14 writes only deviations from its defaults, while 1.12 interprets the same legacy `account_data` slots as complete binding lists, so forwarding them could erase a 1.12 player's keys.

The feature is opt-in. Without all three required environment variables, it makes no network calls and creates no sync journal or backup files; normal local `AccountData` behavior is unchanged.

## Configuration

Configure the process environment, not command-line arguments (arguments are visible in tools such as `ps`):

| Variable | Required | Meaning |
|---|---:|---|
| `ST_SYNC_BASE` | yes | API base, for example `https://stonetavern.app/api/launcher/sync`. HTTPS is required except for `http://127.0.0.1` and `http://localhost`. |
| `ST_SYNC_TOKEN` | yes | Bearer token. The current service issues it for 24 hours and binds it to the account password. It is never logged. |
| `ST_SYNC_DEVICE_ID` | yes | Canonical lower-case UUID for this installation. |
| `ST_SYNC_DEVICE_LABEL` | no | 1–40 printable ASCII characters. The default is `JimsProxy ` plus the OS name; it never uses the machine name. |
| `ST_SYNC_LAUNCHER_VERSION` | no | Sent in `X-Stonetavern-Launcher-Version`. |

An invalid or missing required value disables sync. Startup prints one `account sync: on/off (reason)` line without credentials, keys, hashes, GUIDs, or payloads. Only realms named `Elwynn` or `Barrens` (trimmed, case-insensitive) participate. Only AccountData types 2 and 3 participate; all other types are untouched.

## Data flow

At world entry, before `AccountDataManager.LoadAllData` reads disk, the proxy gets capabilities and the document list. It may restore a remote type 2 or type 3 whole-file payload, then the existing loader proceeds normally. The 1.5-second budget bounds the network part and gates the start of each subsequent local write. Those local writes are small (at most 128 KiB), synchronous file operations and cannot be interrupted after they start. Failure or timeout falls back to local data and never prevents world entry. In `upload-only` mode, restore decisions become no-ops.

After the client atomically saves a type 2 or type 3 file, the packet handler only enqueues it. One background worker debounces each document for five seconds, reads the final file, and reconciles it with a 10-second timeout per request. Network errors, request timeouts, HTTP 429, and 5xx responses re-queue the same key after 30 seconds, 2 minutes, and 10 minutes; after the fourth failure it waits for the next save or world entry. A newer save of that key replaces a pending retry. A save never waits for HTTP and sync exceptions never escape into the packet handler. Shutdown attempts to flush pending work for two seconds and then drops what remains.

Document keys are fixed:

- Type 2: `v1/classic-era-42597/bindings/account/<slug>/-/2`
- Type 3: `v1/classic-era-42597/bindings/character/<slug>/<guid-low>-<guid-high>/3`

The GUID values are unsigned decimal `WowGuid128.GetLowValue()` and `GetHighValue()`, matching the local filename.

## Reconciliation table

`L` is the SHA-256 of the local whole file (or none), `J` is the last-confirmed journal entry (or none), and `R` is the active server head (or none). “Newer” means a different revision id.

| L | J | R | Action |
|---|---|---|---|
| none | any | none | Nothing |
| none | any | present | Restore (nothing in `upload-only`) |
| present | none | none | Upload with `If-None-Match: *` |
| present | none | same hash | Adopt R into J |
| present | none | other hash | POST local as conflict candidate; keep local |
| = J | present | = J | Nothing |
| = J | present | newer than J | Restore (nothing in `upload-only`) |
| != J | present | = J | Upload with `If-Match: <J revision>` |
| != J | present | newer, hash = L | Adopt R into J |
| != J | present | newer, hash != L | POST local as conflict candidate; keep local |

After a 409 response, the proxy re-lists the head. If its hash equals the uploaded bytes it adopts the head; otherwise it posts the bytes as a conflict candidate. A head in `conflict` state blocks restores and normal uploads until the launcher resolves it. Each queued upload gets one `clientUploadId`; PUT and conflict-candidate retries for those bytes reuse it, while a newer save gets a new id.

## Local files and validation

The existing files remain byte-for-byte AccountData containers:

`u64 guidLow, u64 guidHigh, i64 timestamp, u32 type, u32 uncompressedSize, i32 compressedSize, byte[compressedSize]`, little-endian, followed by zlib data.

The proxy uploads and restores the whole file. Before restore it verifies the advertised SHA-256 and byte length, type, the type 3 GUID, compressed length, zlib stream, exact inflated length, 128 KiB whole-file limit, and 1 MiB inflated limit. Save and restore share one process-wide synchronous lock per AccountData directory, plus the cross-process lock file described below. Under that lock, restore re-reads and hashes the local file, makes the backup, and replaces the file; a save that arrives during this final section waits and writes its newer bytes afterward. If the hash differs from the one used for the reconciliation decision, restore stops, keeps the concurrent local save, and leaves the journal unchanged.

The feature can create these items beneath `AccountData/<ACCOUNT>/<Realm>/`:

- `sync-journal.json`: schema v1, atomically replaced, recording revision and hash by document key. A corrupt or unknown-version journal is treated as absent.
- `sync-backups/<filename>.<unix-ms>.bak`: the previous local file before a restore. The newest ten backups per source filename are retained.
- Short-lived `.tmp`/`.sync.tmp` files used for atomic replacement; successful, failed, and budget-cancelled writes clean them up.

A missing local file is never treated as deletion. Network errors, malformed or oversized responses, invalid payloads, 413, 429, and timeouts keep local data. A 401 or 403 disables sync for the rest of the process. HTTP 413 and other 4xx responses except 409/429 are not retried. No token, payload, hash, or document key is logged.

## Lock shared with the launcher

The Stonetavern launcher may replace a type 2/3 file before it starts the proxy (a conflict choice or an undo the player queued). To keep that write and a proxy write apart, both sides open `.st-sync.lock` in the AccountData realm directory (`AccountData/<account>/<realm>/`) with `FileShare.None` and hold it for the final check, backup and move. On Windows that is a share mode, on Linux and macOS .NET takes a `flock` on the file, so the lock works between the two processes and through the macOS `Hermes/bin/AccountData` symlink (it locks the file, not the path). Both processes always run on the same machine, so a local lock is enough even when the game folder sits on a network share. The operating system releases the lock when a process dies; the file is never deleted, so both sides always lock the same file.

**No final file is ever written without the lock.** The waiting side polls for the lock file without holding the in-process gate, then takes the gate, so no other writer of this process waits behind a poll.

| Side | Waits | When the lock stays busy |
|---|---|---|
| Proxy, client save (`SaveData`) | up to 1 s | writes `<file>.deferred` and a background thread moves it into place once the lock frees; a newer save under the lock supersedes it; `LoadData` reads the pending one; log `account sync: the AccountData lock stayed busy, the save is written as soon as it frees` |
| Proxy, restore at world entry | up to 2 s, cut short by the 1.5 s world-entry budget | leaves the file and the journal alone; also stands down while a deferred save of this process is pending |
| Proxy, world entry | up to 2 s | a save a process that died could not write (`.deferred`, or a whole `.deferred.tmp`, which is newer) is put in place, the file it replaces goes to `sync-backups/<file>.<unix-ms>.bak`; a cut-off temp file is kept in `sync-backups` and never used; busy lock: next world entry |
| Proxy, shutdown | up to 1 s | tries the pending deferred saves once more; what is left is recovered as above at the next world entry |
| Launcher, queued replace or undo | up to 2 s | writes nothing and keeps the choice queued for the next Play; the same while a `.deferred` or `.deferred.tmp` of that document exists |

**Order of saves.** Each save takes a sequence number before it waits for any lock and writes the final file only if no later save has (checked under the gate), so a save that was held up never replaces newer bytes. A deferred save obeys the same rule when it is moved into place. The gate wait is bounded for restore, recovery and the deferred worker (they give the file lock back and write nothing when the gate stays busy); a client save waits for the gate, because it must end up written or deferred.

The launcher checks again, while it holds the lock, that no client and no proxy process is running before it writes, and lists one physical realm folder only once (symlinks resolved; an alias that still slips through makes the second lock open fail, so nothing is written twice). `DOTNET_SYSTEM_IO_DISABLEFILELOCKING` (or the `System.IO.DisableFileLocking` switch) makes .NET skip the lock, measured on Linux: the proxy then turns account sync off (`file locking is disabled for this process`), the launcher removes the variable from the proxy's environment and does not write AccountData while it is set for the launcher itself. The file name is pinned by a test on both sides (`DirectoryLock_FileNameIsTheOneTheLauncherUses` here, `The_file_name_is_the_one_the_proxy_uses` in the launcher); change it only together.

Not measured: macOS (APFS flock) and Windows share modes between the two real binaries; the Linux behaviour is measured with `flock(1)` as the second process.

## Required server API

All requests use `Authorization: Bearer <token>`, JSON bodies, and the configured base URL. If configured, the launcher version is sent as `X-Stonetavern-Launcher-Version`.

Responses are requested with headers-first streaming and fully bounded before JSON parsing. A declared `Content-Length` above the limit is rejected without reading the body, and an unbounded/chunked stream is cut off when it crosses the same limit: document GET 256 KiB, document list 1 MiB, and capabilities/write responses 64 KiB.

### Capabilities and listing

- `GET <base>/capabilities` returns `{ "mode": "off"|"upload-only"|"on", "families": { "bindings": true|false }, "apiVersion": 1, "limits": {...} }`. Bindings run only for API v1, an enabled bindings family, and a non-off mode.
- `GET <base>/documents` returns `{ "documents": [{ "key", "activeRevision": "r12"|null, "sha256", "byteLength", "state": "active"|"conflict", "updatedAt", "conflictCandidates": [...] }] }`.
- `GET <base>/documents/<key>` returns `{ "revision", "sha256", "payloadB64", "byteLength", "kind", ... }`, or 404. Canonical key slashes remain path separators; they are not percent-encoded.

### Writes

- `PUT <base>/documents/<key>` uses `If-Match: <revision>` or `If-None-Match: *` and body `{ "formatVersion": 1, "sha256", "payloadB64", "clientUploadId": "<UUID stable across retries>", "deviceId", "deviceLabel" }`.
- `POST <base>/documents/<key>/conflict-candidates` uses the same body without a precondition.
- Successful writes return 200 and a document head (optionally including `repeated`). A PUT conflict returns 409 with conflict details. The service may return 401, 403 `sync_disabled`, 413, 428, or 429; the proxy's behavior is described above.

The server must enforce the same canonical keys and container limits. It should treat `clientUploadId` idempotently so a retry after the server persisted a write but lost the response is safe.

## Why restoring at world entry works (measured)

On a local test server (2026-09-28, test account only) the 1.14.2 client took whatever `data-2.bin` the proxy
served at world entry: a newer file replaced its bindings, and so did a file with an **older** timestamp. The client
does not compare timestamps with its own state, so the proxy can simply put the file in place before
`LoadAllData` reads it. Measured again on 2026-09-30 with this build and the real client (headless, test account, local server):
PC A enters the world, the bindings F9 = JUMP and F10 = TOGGLEWORLDMAP are set and saved on logout, the proxy
uploads `data-2.bin` (78 bytes). PC B is a **fresh client process** with an empty proxy folder and no local
`WTF/Account/<ACCOUNT>` folder: at world entry the proxy restores the byte-identical file and the client reports
`JUMP=SPACE,NUMPAD0,F9` and `MAP=F10,M`. Control run, same setup without the `ST_SYNC_*` variables:
`JUMP=SPACE,NUMPAD0`, `MAP=M`. Not measured: a client that keeps a stale local `WTF` folder, and the direction
1.12 → 1.14 (not a use case: 1.12 bindings are never sent here).

## End-to-end test against a real server

`AccountSyncLiveServerTests` plays two PCs of one account against a running sync server. It is skipped unless
`ST_SYNC_E2E_BASE` and `ST_SYNC_E2E_TOKEN` are set; the server must have sync `on` for that account and no documents
for it yet. It covers: first upload from PC A, restore of the exact bytes on an empty PC B, a follow-up change from A
restored on B (with backup), and two offline edits ending as a conflict that overwrites nobody. Use a test account on
a local or staging server only.

```sh
ST_SYNC_E2E_BASE=http://127.0.0.1:3399/api/launcher/sync ST_SYNC_E2E_TOKEN=<token> \
  dotnet test HermesProxy.Tests --filter "FullyQualifiedName~AccountSyncLiveServerTests"
```

## Maintenance, tests, and removal

All implementation files are under `HermesProxy/World/Server/AccountSync/`. Existing-file touch points are deliberately marked:

```sh
grep -rn "ACCOUNT-SYNC" HermesProxy
```

Run the focused tests with the xUnit v3 in-process runner:

```sh
dotnet run --project HermesProxy.Tests -- --filter-namespace HermesProxy.Tests.World.AccountSync
```

Run the complete suite with `dotnet test HermesProxy.Tests` in a normal environment. In restricted sandboxes where the VSTest socket cannot bind, use `dotnet run --project HermesProxy.Tests`.

To disable the feature, remove any one required `ST_SYNC_*` variable and restart. To remove it from the fork, delete the `AccountSync` source and test folders, remove the marked startup/load/save/shutdown lines and their `using` directives, and delete this document and the corresponding changelog entry. Existing journals and backups are then inert and may be deleted separately.

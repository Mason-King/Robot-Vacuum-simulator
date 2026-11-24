# Network worker protocol (version 1)

The interface between the simulator on a researcher's PC (the **client**) and the simulator in worker mode on another
computer (the **worker**). Requirement **3.9**: two independently executable components over a defined interface, one
across a network boundary. Security: **4.8**, **C.12**. Code: `Robot vacuum simulator/Assets/Scripts/Network/`.

Both sides are the same simulator build. The worker is started from Settings › *Use this PC as a worker*, or
by hand:

```
RobotVacuum.exe -batchmode -nographics -worker [-workerPort 47815] [-workerProcesses N] [-workerPairMinutes 10]
```

| Flag | Default | Meaning |
|---|---|---|
| `-worker` | | Worker mode instead of the GUI or a single job. Can't be combined with `-configPath`. |
| `-workerPort` | 47815 | TCP port to listen on (all network interfaces). |
| `-workerProcesses` | CPU cores - 1 | Most simulations run at once, across all clients. |
| `-workerPairMinutes` | 10 by hand, 0 from Settings | Opens pairing at start and prints the code. |
| `-workerParentPid` | | Set by Settings: the worker stops when that simulator closes. |

## What goes where

```
 Researcher's PC (client)                              Worker PC
 ────────────────────────                              ─────────
 Analysis ◄─ import ─ campaign folder                  worker process (listens, never runs job code)
                       ▲                                 │  per job: validation process (-validateOnly)
 batch dialog ─► job.json ── submit ──────────────────►  │  per shard: headless process (-configPath job.json
                       ▲     status (every few s) ◄────  │            -outputPath shard -shardIndex i -shardCount n)
                       └──── list / get files ◄──────  clients/<client>/jobs/<job>/shards/shard-NNNN/
                                                         results.json, heartbeat.json, logs/unity.log, runs/<guid>/
```

The job file is the headless job format (`job-format.md`), the same one cloud nodes run. A worker's shard folder holds
exactly what a cloud node uploads (`job-format.md` › What a node writes), so the client imports worker results with the
same code as cloud results.

## Transport and framing

- TCP. Every message is a **frame**: a 4-byte big-endian length, then that many bytes (at most 16 MB).
- The first frame each way is plain JSON (the handshake). Every later frame goes through the secure channel.
- A connection is closed after 120 s with no message, or at the first message that fails authentication.
- At most 16 connections at a time; more are closed straight away.

## Handshake

Client → worker, `Hello`:

```json
{ "protocol": 1, "mode": "session", "clientId": "4f6c…", "clientName": "KRISTIE-PC",
  "clientNonce": "<base64 of 32 random bytes>", "appVersion": "0.1.0" }
```

`mode` is `pair` (first contact, with a pairing code) or `session` (with the key saved at pairing).

Worker → client, `Welcome`:

```json
{ "protocol": 1, "workerId": "9a1e…", "workerName": "LAB-PC-3", "appVersion": "0.1.0", "buildId": "…",
  "jobSchemaVersion": 1, "serverNonce": "<base64 of 32 random bytes>", "error": "" }
```

| `error` | Meaning (the client shows it in words) |
|---|---|
| `version` | Different simulator versions: a run must give the same result wherever it runs (2.1, 2.17), so the worker refuses. |
| `pairing-closed` | Pairing is not open on the worker. |
| `not-paired` | This client has no key on the worker (never paired, or removed). |
| `protocol`, `bad-hello` | Incompatible or malformed. |

Neither message contains anything secret.

## Pairing

1. The person at the worker opens pairing (Settings › *Allow a new PC to pair*, or `-workerPairMinutes`). The
   worker shows a code such as `K7QFM-2X9RT`: 10 characters from `ABCDEFGHJKMNPQRSTUVWXYZ23456789` (about 49 bits).
2. On the client the researcher enters the worker's address and the code. The code is **never sent**. Both sides
   compute `pairingKey = PBKDF2-HMAC-SHA256(code, "robot-vacuum-sim/pair/v1" ‖ clientNonce ‖ serverNonce, 100 000, 32 bytes)`
   and open the secure channel with it.
3. The client sends `pair` `{ "clientName": "…" }`. Only a client with the right code produces a message the worker
   accepts. The worker replies `paired` `{ "workerId", "workerName", "key" }` with a fresh random 32-byte long-term key
   for this client, and closes pairing (one PC per code).
4. Both sides store the key in **Windows Credential Manager** (`RobotVacuumSim/worker-client-<clientId>` on the worker,
   `RobotVacuumSim/worker-<workerId>` on the client); on Linux workers, in a file only that user can read. Never in
   PlayerPrefs, settings files or job files (C.12).

A wrong code just closes the connection. Pairing closes after 10 minutes or after 5 wrong codes. Capturing a pairing
exchange doesn't help an attacker: guessing the code offline costs 100 000 HMAC rounds per guess over about 2^49 codes.

## Secure channel

For every connection both sides derive four keys from the secret (pairing key or long-term key) and the two nonces:

```
key(label) = HMAC-SHA256(secret, label ‖ 0x00 ‖ clientNonce ‖ serverNonce)
labels: c2s-enc, c2s-mac, s2c-enc, s2c-mac
```

Each message is encrypted with AES-256-CBC (PKCS#7, a random 16-byte IV), then authenticated (encrypt-then-MAC):

```
frame = IV ‖ ciphertext ‖ HMAC-SHA256(macKey, counter(8 bytes, big-endian) ‖ IV ‖ ciphertext)
```

The counter starts at 0 in each direction and is not sent. A changed, replayed, reordered or reflected message fails the
MAC and the connection is closed. Fresh nonces mean fresh keys on every connection.

The plaintext of a message is `headerLength (4 bytes) ‖ header JSON ‖ binary payload`, where the header is
`{ "type": "<type>", "body": "<JSON of the request or reply>" }`.

## Requests

After the handshake the client's first message must be `auth` `{}`; the worker answers `info`. Then any number of:

| Request | Body | Payload | Reply |
|---|---|---|---|
| `info` | `{}` | | `info` `{ workerId, workerName, appVersion, buildId, platform, processorCount, maxProcesses, runningProcesses, queuedShards }` |
| `submit` | `{ jobId, campaignName, shardCount, shards: [i…], resume: [{ shardIndex, resultsJson }] }` | job.json (UTF-8, ≤ 12 MB) | `submitted` `{ jobId }` |
| `status` | `{ jobId }` (empty: all of this client's jobs) | | `status-reply` `{ jobs: [{ jobId, campaignName, state, submittedUtc, errors[], shards: [{ shardIndex, state, totalRuns, completedRuns, failedRuns, exitCode, message, updatedUtc }] }] }` |
| `list` | `{ jobId, shardIndex }` | | `files` `{ files: [{ path, size }] }` (paths relative to the shard folder) |
| `get` | `{ jobId, shardIndex, path, offset, length }` (length ≤ 4 MB) | | `chunk` `{ path, offset, totalSize }` + the bytes |
| `cancel` | `{ jobId }` | | `ok` |
| `release` | `{ jobId }` | | `ok` (the job's folder is deleted on the worker) |
| `unpair` | `{}` | | `ok`, then the worker deletes this client's key and jobs and closes the connection |

Any request can instead get `error` `{ code, message }`, with a message meant for the researcher.

- `submit` with an id the worker already has adds shards to that job (used when the client moves shards from a worker
  that dropped out). The job file must be identical (SHA-256), or it is refused.
- `resume` carries a shard's `results.json` from wherever it ran before; the shard then skips the runs already done
  (4.5 failure recovery).
- Results come back in steps: the client lists each shard's files every few seconds and downloads the run folders it
  doesn't have yet (SDD 8). `get` on `logs/unity.log` with a growing offset follows the log (3.12).

### Job and shard states

```
validating ──► queued ──► running ──► finished
     │            │           └─────► failed     (exit code 1, or the process could not start)
     └─► rejected (validation failed: the job's errors are in status.errors)
  any ──► cancelled (cancel, release, unpair)
```

Shard exit codes are those of the headless build: 0 all runs succeeded, 10 finished with some failed runs (still
`finished`; see results), 2 rejected, 1 crashed.

## Trust boundaries (4.8)

| Boundary | Enforced by |
|---|---|
| Network ↔ worker | Only paired clients get past the handshake; every message is authenticated; ids are plain names (`[A-Za-z0-9][A-Za-z0-9._-]{0,79}`). |
| One client ↔ another | Each client's jobs are in its own folder; every request is answered only from the asking client's jobs. A client can't list, read, cancel or delete another's. |
| Requests ↔ worker's disk | `get` only reads files inside the client's own shard folder (no `..`, no absolute paths, no drive letters). |
| Worker process ↔ job code | The listening process never compiles or runs a job's algorithms. Validation and shards run in separate headless processes at below-normal priority, and algorithm code in them is behind the plugin sandbox (`Algorithms/README.md` › The sandbox). |
| Version ↔ results | A worker on a different simulator version is refused, so a run gives the same result on any worker (2.1, 2.17). |

Not covered: the connection is between computers the researcher controls on a trusted network. There is no
certificate authority; the pairing code is what ties a worker to a client. The worker log is `Worker/worker.log` in the
simulator's data folder (`%USERPROFILE%\AppData\LocalLow\DefaultCompany\Robot vacuum simulator\Worker` on Windows):
connections, refusals, wrong pairing codes, submissions and every shard start and exit.

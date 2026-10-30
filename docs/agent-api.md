# Scion agent API quickstart

Scion is server-centric: an agent never clones. It authenticates once, asks the server for
history and diffs, ships changed files only, and never touches a lock. This document is the
quickstart for that loop — every example here is executed against a test server by
`AgentApiParityTests`, and `OpenApiContractTests` holds this reference table against the
generated document at `/openapi/v1.json`. If this document and the tests disagree, one of them
is wrong and CI is red.

The machine-readable contract is the OpenAPI document itself:

```
GET /openapi/v1.json          # anonymous; describes every operation below
```

## Authentication

Every `/rpc` endpoint requires a bearer token; `/healthz` and `/openapi/v1.json` are anonymous.
The server refuses to start with no token configured.

```
Authorization: Bearer $SCION_TOKEN
```

Without it (or with a wrong one), every endpoint answers:

```
401 Unauthorized
WWW-Authenticate: Bearer
{"type":"about:blank","title":"Unauthorized","status":401,"code":"unauthorized"}
```

## The commit loop

A commit is two calls: upload the changed files' content, then append one changeset that
references it. Cost scales with the change, never with the repository.

### 1. Check the server is alive (optional)

```
GET /healthz → 200 {"status":"ok"}
```

### 2. Upload each changed file's content

The route names the content's SHA-256; the body is the raw bytes. The server verifies the body
hashes to the named address before storing, so content can never be stored under an address it
does not match.

```
POST /rpc/blobs/8f4a3c…0e1     (the 64-hex SHA-256 of the body)
Content-Type: application/octet-stream

<file bytes>
→ 201 {"hash":"8f4a3c…0e1"}    (201 = newly stored; a re-upload of known content answers 200)
```

Uploads are idempotent and safe to retry.

### 3. Append the changeset

Send the changeset record — parents, the paths changed relative to the first parent, and the
commit metadata — plus any blobs you have not already uploaded. `id` is the SHA-256 of the
canonical payload encoding (camelCase field order: parents, changes, message, author, timestamp —
see `CanonicalChangesetSerializer`); the server re-derives it and rejects mismatches, so a client
cannot commit a payload that hashes differently from its claim.

```
POST /rpc/changesets
{
  "id": "9c1e77…f3b",
  "parents": ["5b02aa…c1d"],
  "changes": [
    {
      "path": "src/agent.cs",
      "oldBlob": null,
      "newBlob": "8f4a3c…0e1",
      "kind": "regular",
      "type": "added"
    }
  ],
  "message": "add agent",
  "author": { "name": "Agent", "email": "agent@example.test" },
  "timestampUtc": 1700000000,
  "blobs": [ { "hash": "8f4a3c…0e1", "content": "<base64>" } ],
  "refMove": { "name": "main", "expectedRevision": 7 }
}
→ 201
{
  "id": "9c1e77…f3b",
  "ref": { "name": "main", "target": "9c1e77…f3b", "revision": 8 }
}
```

Fields:

- `changes` — relative to the **first parent only**. `type` is one of `added`, `modified`,
  `deleted`, `renamed`; `kind` is one of `regular`, `executable`, `symlink`. `oldBlob` is null
  for additions, `newBlob` is null for deletions.
- `blobs` — optional convenience: content uploaded inline in the same request, instead of (or as
  well as) the dedicated endpoint above. Content is stored before the log appends.
- `refMove` — optional. Publishing a ref is a compare-and-swap on a single row, decoupled from
  the append: `expectedRevision` is the ref's revision the client last saw (0 creates a ref that
  does not exist). **Append and CAS are independent** — parallel agents never contend on commit.

**Ref conflict (409).** If the ref moved between your read and your write, the changeset is
still stored — append never fails for a lost move — and the 409 carries the ref's current value
so you can rebase and retry without a second round trip:

```
409 Conflict
{
  "type": "about:blank",
  "title": "Ref conflict",
  "status": 409,
  "code": "ref_conflict",
  "ref": "main",
  "currentTarget": "d4188f…9a2",
  "currentRevision": 8
}
```

Recovery: re-read the changeset you committed (`GET /rpc/changesets/{id}` — it is there), rebase
its delta onto `currentTarget` (POST /rpc/rebase), and move the ref again with
`expectedRevision: currentRevision`.

## Reading state — zero local computation

Every history query is one server round trip.

```
GET /rpc/changesets/{id}          → 200 {"id","parents","changes","message","author","timestampUtc"}
                                  → 404 code:"not_found", objectType:"changeset", objectId

GET /rpc/log?tip={id}&after={cursor}&limit={n}
                                  → 200 {"changesets":[…],"nextAfter":…|null}
```

`/rpc/log` walks first-parent lineage newest-first. `limit` is 1–1000 (default 100); anything
else is a 400. `nextAfter` is the cursor for the next page — pass it back as `after` — and null
at the end of the lineage.

```
GET /rpc/path-history?changeset={id}&path={path}
                                  → 200 {"path":"…","changesets":["oldest","…","newest"]}

GET /rpc/diff?from={id}&to={id}   → 200 {"from","to","changes":[{"path","type","oldBlob","newBlob","kind"}]}

GET /rpc/blame?changeset={id}&path={path}
                                  → 200 {"changeset","path","lines":[{"line","changesetId","content"}]}

GET /rpc/merge-base?first={id}&second={id}
                                  → 200 {"mergeBase":"…"|null}
```

`/rpc/blame` attributes every line to the changeset that introduced it; `content` keeps the
terminating newline. `/rpc/merge-base` answers `null` (not an error) for disjoint histories.

## Server-side merge and rebase

Merges and rebases run on the server; the result is new changesets — nothing to push.

```
POST /rpc/merge
{
  "ours":   "d4188f…9a2",
  "theirs": "71c0ee…44b",
  "message": "merge feature",
  "author": { "name": "Agent", "email": "agent@example.test" },
  "timestampUtc": 1700000500,
  "refMove": { "name": "main", "expectedRevision": 8 }
}
→ 201 {"mergeBase":"5b02aa…c1d","id":"e10dc4…77f","ref":{…},"conflicts":[]}
```

A clean merge appends one merge changeset — first parent ours, second theirs. **Conflicts answer
409 with the full resolution vocabulary and nothing appended** — the path, why (`content`,
`deleteModify`, `renameRename`, `renameDelete`), each side's blob, and, for content conflicts, the
merged text with `<<<<<<<`/`=======`/`>>>>>>>` markers so an agent can resolve without recomputing
the merge:

```
409 Conflict
{
  "mergeBase": "5b02aa…c1d",
  "id": null,
  "conflicts": [
    {
      "path": "docs/shared.md",
      "kind": "content",
      "baseBlob": "b1…", "oursBlob": "c2…", "theirsBlob": "d3…",
      "mergedContent": "<<<<<<< ours\nshared: main side\n=======\nshared: branch side\n>>>>>>> theirs\n"
    }
  ]
}
```

Rebase replays a first-parent chain — `start` (inclusive) through `tip` (inclusive, must extend
start) — onto `newBase`. The replay never aborts: conflicted paths keep the new base's state and
the conflicts ride the 201 response, one per source step (`sourceIndex`, `sourceId`, `rebasedId`,
and the same conflict vocabulary as merge):

```
POST /rpc/rebase
{ "start": "71c0ee…44b", "tip": "71c0ee…44b", "newBase": "d4188f…9a2" }
→ 201
{
  "newBase": "d4188f…9a2",
  "rebased": [ { "sourceId": "71c0ee…44b", "id": "af31c0…2be" } ],
  "conflicts": [
    { "sourceIndex": 0, "sourceId": "71c0ee…44b", "rebasedId": "af31c0…2be",
      "conflict": { "path": "…", "kind": "content", … } }
  ],
  "ref": { "name": "feature", "target": "af31c0…2be", "revision": 9 }
}
```

Both endpoints accept an optional `refMove` (compare-and-swap, same contract as commit) and
answer 400 for validation failures, 404 for unknown ids.

## The fetch protocol: ask for what you want, never negotiate what you have

An agent materializes a working copy — or any subtree of one — in two calls, walking the wanted
tree and requesting only what its cache lacks. Sparse and virtualized checkouts need no special
support: the client simply never asks for paths outside its profile.

```
POST /rpc/trees/missing
{ "changesetId": "d4188f…9a2", "wantedPaths": ["src/**"] }
→ 200
{
  "rootTree": "t0f8ec…12a",
  "presentBlobs": ["8f4a3c…0e1"],
  "missingBlobs": ["c4103b…5d2"]
}
```

Name the tree by `changesetId` or by its own content hash `rootTree` — exactly one of the two.
`wantedPaths` are globs scoping the walk (omit for the whole tree); `sparseProfile` narrows
further. Both blob lists are distinct, lowercase, ordinal-sorted.

Then transfer the missing blobs — or any hashes you want to prefetch — in one streaming call:

```
POST /rpc/blobs/missing
{ "blobs": ["c4103b…5d2", "8f4a3c…0e1"] }
→ 200 application/octet-stream
```

The response is a length-prefixed stream: per requested hash, in request order — the 64 ASCII
bytes of the lowercase-hex hash key, then chunks of 8-byte unsigned little-endian length plus
that many content bytes, closed by a zero-length chunk. Duplicates are served once. Any unknown
hash answers 404 (as a JSON problem, before any stream starts), any malformed hash 400.

## Error contract

Every error body is a JSON problem with a stable `code` an agent switches on — never prose
matching. Statuses the endpoints can answer are asserted per operation by
`OpenApiContractTests.StatusContract`; the vocabulary:

| Code             | Status | Meaning |
| ---------------- | ------ | ------- |
| `validation`     | 400    | The payload violates the contract — wrong shape, lying changeset id, hash mismatch, unstored blobs, unknown query combination. Fix the request. |
| `malformed_body` | 400    | The body could not be parsed as JSON at all (syntactically broken, or empty where an object is required). Fix the payload; this is never a server fault. |
| `not_found`      | 404    | A named object (changeset, blob, tree) does not exist. The body adds `objectType` and `objectId`. |
| `ref_conflict`   | 409    | A CAS ref move lost the race; `ref`, `currentTarget`, `currentRevision` carry the current value. Rebase and retry. |
| `unauthorized`   | 401    | Missing, malformed, or wrong bearer token. |

## Endpoint reference

| Operation | Success | Errors |
| --------- | ------- | ------ |
| `GET /healthz` | 200 | — |
| `POST /rpc/blobs/{hash}` | 201 new, 200 existing | 400 |
| `POST /rpc/changesets` | 201 | 400, 409 |
| `GET /rpc/changesets/{id}` | 200 | 404 |
| `GET /rpc/log` | 200 | 400, 404 |
| `GET /rpc/path-history` | 200 | 400, 404 |
| `GET /rpc/diff` | 200 | 400, 404 |
| `GET /rpc/blame` | 200 | 400, 404 |
| `GET /rpc/merge-base` | 200 | 400, 404 |
| `POST /rpc/merge` | 201 | 400, 404, 409 |
| `POST /rpc/rebase` | 201 | 400, 404 |
| `POST /rpc/trees/missing` | 200 | 400, 404 |
| `POST /rpc/blobs/missing` | 200 | 400, 404 |

Every `/rpc` operation additionally answers 401. The full request/response schemas — including
every field's type — live in `/openapi/v1.json`, generated from the same route table this table
summarizes.

# Object storage

A market archive keeps Parquet data under `{kind}/{encoded-key}/segments/{opaque-id}.parquet`,
with immutable `commits/{opaque-id}.json` manifests containing ranges, counts, hashes and order.
Where those objects live is a choice: a directory on the machine reading them, or
S3-compatible object storage for a catalog larger than any one machine's disk
(R9.5).

The layout, the file names and every guard over them are the catalog's, not the
file system's, so both work the same way and are tested the same way.

## Naming a catalog

Everywhere a catalog is named — a command line, a backtest's data configuration,
a strategy document — one string names it:

```bash
bytex catalog info --path /var/lib/bytex/catalog
bytex catalog info --path s3://my-bucket/catalogs/eu
```

A path is a path. Anything with a scheme is looked up in a registry of backends,
and a host turns one on with a line at startup:

```csharp
S3Location.Register();   // from Bytex.Persistence.S3
```

That is a line rather than something that happens on its own: a library that
registered itself the moment it loaded would be deciding for the program that
loaded it where that program's data lives. The CLI calls it, so every `bytex`
command accepts `s3://` already. A location with a scheme nobody registered says
which package provides it rather than quietly making a directory called `s3:`.

A Windows drive letter is not a scheme — `C:\data` has its colon in the same
place `s3://bucket` does, so a scheme is only read where the colon is followed by
two slashes.

## Reaching the service

```
s3://bucket/prefix
s3://bucket/prefix?region=eu-west-1
s3://bucket/prefix?endpoint=http://localhost:9000&path-style=true
```

| Option | From the location | From the environment |
|---|---|---|
| Region | `?region=` | `AWS_REGION`, then `AWS_DEFAULT_REGION`, else `us-east-1` |
| Endpoint | `?endpoint=` | `AWS_ENDPOINT_URL_S3`, then `AWS_ENDPOINT_URL` |
| Bucket in the path | `?path-style=` | path style for a named endpoint, host style for AWS |
| Credentials | — | `AWS_ACCESS_KEY_ID`, `AWS_SECRET_ACCESS_KEY`, `AWS_SESSION_TOKEN` |

The environment variables are the ones every AWS tool already reads, so a machine
set up for one is set up for this. Credentials are resolved when the first
request is signed rather than when a location is read: naming a catalog is not the
same as having keys for it, and a configuration is printed, checked and passed
around in places that never read a byte.

Anything speaking the S3 API serves this — AWS, another provider, or something
running beside the node.

## What it costs

**Parquet is read from a footer at the end of the file, so reading one object
means fetching the whole object.** A remote catalog is therefore paid for per
query. Keeping data beside the machine that reads it is faster and always will
be; what object storage buys is a catalog larger than one machine's disk, shared
by everything that reads it.

For repeated work over the same period — a parameter sweep, a day of iteration —
download once into a local catalog and read from there.

## What is written, and how

- **An object appears only once it is whole.** A segment becomes readable only
  after its complete manifest is published. A failed batch leaves uncommitted
  objects for inspection, never a partly readable batch.
- **A move is a copy and a delete**, because object storage has no rename. The
  copy happens inside the service; the bytes are not fetched and sent back. This
  is a store primitive, not the consolidation commit protocol. Consolidation
  publishes a replacement manifest and retains superseded segments for recovery.
- **One object per PUT, and a size limit that is refused by name.** A multipart
  upload is not written here, so an object above the single-request limit (5 GiB
  by default) is refused with what to do about it, rather than sent in part. The
  catalog reads any number of files per key, so writing in more than one is the
  answer.
- **Large objects are spooled to a temporary file** rather than held in memory,
  above 64 MiB either way. The file deletes itself when it is closed.
- **A listing is recursive, ordered by key, and follows the continuation token.**
  A reader that ignored the token would see the first thousand keys of a catalog
  and call it the catalog.

## Signing

Signature Version 4 is written here rather than taken from a vendor SDK. The whole
of what a catalog needs is five requests — GET, PUT, HEAD, DELETE and a paginated
list — and a vendor's client brings a dependency tree larger than this repository
to serve them. The signing is the only hard part, it is specified rather than
guessed, and it fails completely rather than plausibly: a service answers
`SignatureDoesNotMatch` and nothing happens.

A secret never leaves the signing key; what is sent is the access key, the date,
the region and a signature.

## What has been verified

- **Against a real service.** The whole catalog — write, read, list, `info`,
  `check`, `consolidate` and a streaming read — over object storage, including a
  key holding characters that have to be encoded, a listing over several pages,
  and a move. These tests are **skipped, not silently passed**, where there is no
  service: set `BYTEX_TEST_S3` with the two credential variables to include them.
- **Against the wire.** What the store sends, checked without a service: that the
  body's hash is what is signed, that the host is among the signed headers, that a
  move copies inside the service, that an oversized object is refused before
  anything is sent. These exist because a lenient service hides mistakes — two
  deliberately broken signatures were accepted by the service used for the
  integration tests and would be refused by AWS.
- **Not against AWS itself.** Nothing here has been run against AWS S3, and no
  other provider's quirks have been met yet. The API is the same API; the
  differences that remain are theirs.

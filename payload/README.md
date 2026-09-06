# Bundled payload staging

Release automation stages architecture-specific Paqet, Xray, and ProxiFyre artifacts
under `payload/engines/`. Driver installers that have approved redistribution
rights are staged under `payload/prerequisites/`.

These directories are intentionally ignored by Git. Do not commit downloaded
binaries, credentials, generated engine configuration, or proprietary driver
packages. A release build must:

1. download artifacts from pinned first-party release URLs;
2. verify their expected SHA-256 values before extraction;
3. inventory every installed file in `payload-manifest.json`;
4. preserve all upstream license and notice files;
5. fail closed when a version, architecture, signature, or digest differs.

`release-assets.json` locks the official upstream archive URLs and archive
SHA-256 values. From the repository root, stage the complete verified x64
payload with:

```powershell
.\scripts\Stage-ReleasePayload.ps1
```

The command refuses to write into a non-empty destination. This avoids silently
mixing payload versions; remove an old ignored `payload/engines/` directory
before deliberately restaging it.

`manifest.template.json` documents the runtime schema. It contains no approved
versions or hashes and is not a release lock file.

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

`manifest.template.json` documents the runtime schema. It contains no approved
versions or hashes and is not a release lock file.

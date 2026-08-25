# 5. Transactional state, backup, and recovery

Required authoritative extension state belongs in the kernel-provided
transactional store. External databases are rebuildable projections and do not
participate in the atomic backup guarantee.

## Public state contract

A strict manifest can declare one schema name, positive schema version, and
requiredness. The extension separately requests `extension-state.read` and/or
`extension-state.write`. `ITransactionalStateCapability` exposes:

- typed read with an opaque numeric concurrency token;
- create/update with compare-and-swap;
- compare-and-swap delete;
- bounded, ordinal key listing under an owner-local prefix.

A null expected token means create-only, not unconditional overwrite. Stale or
missing records return typed outcomes. Durable deletion writes a version-2
`write.commit` tombstone as its commit point: failure or cancellation before
that point retains the original value and token; interruption afterward rolls
the deletion forward during recovery. The token is internally called an ETag,
but it is not an HTTP `ETag` header. It is store-wide, monotonic across restart,
may contain gaps, and must be treated as opaque.

The public API has no general multi-key transaction and no migration callback
registration. A public extension must not increment a persisted schema and
assume the kernel can run extension-supplied transforms.

## Internal store behavior

[`TransactionalStateStore`](../../src/NuExtVault.Kernel/Kernel/Capabilities/TransactionalStateStore.cs)
owns persistence, schema validation, atomic internal multi-key edits, quotas,
fixed-cardinality locks, checkpoints, and crash recovery. Internal participant
migrations must form complete adjacent `n -> n+1` chains. Newer schemas, changed
names, incomplete chains, and transform failures fail without changing
authoritative state.

Default bounds are 128 characters per key, 64 MiB per record, 256 records and
256 MiB per owner, 64 owners, 64 lock stripes, a ten-minute checkpoint lease,
and an 8 KiB record-header read bound. Opening and checkpoint capture inspect
bounded metadata and stream payloads rather than loading the whole store.

[`ExtensionStateIntegrationTests`](../../tests/NuExtVault.UnitTests/ExtensionStateIntegrationTests.cs),
[`ExtensionStateBoundednessTests`](../../tests/NuExtVault.UnitTests/ExtensionStateBoundednessTests.cs),
and [`ExtensionStateHardeningTests`](../../tests/NuExtVault.UnitTests/ExtensionStateHardeningTests.cs)
cover concurrency, restart monotonicity, quotas, lock cardinality, migration,
corruption, and recovery.

## Checkpoints and backup

The state checkpoint freezes every participant while all lock stripes are held.
Durable stores copy committed records into a leased checkpoint directory;
in-memory stores retain an immutable snapshot. Mutation after capture cannot
alter exported content.

Backup/restore operations are owned by the official `builtin.operations` module,
but the kernel retains file-handle, checkpoint, validation, and commit authority.
[`StorageBackup`](../../src/NuExtVault.Kernel/Operations/StorageBackup.cs)
creates version-2 manifests containing participant identity, schema,
requiredness, record count, and SHA-256 integrity.

Backup is offline. It acquires the exclusive durable-storage lease, so a running
durable server makes backup unavailable. Restore requires a clean target and
validates paths, lengths, hashes, free space, schemas, required participants, and
both directions of the manifest/state relationship before commit.

Unknown or missing required participants, newer schemas, incomplete migrations,
undeclared state, mirror divergence, unsafe paths, and foreign journals fail
closed. Optional inactive state is quarantined rather than activated or deleted.
Version-1 archives remain readable and are adopted on the next store open.

## Recovery journals

Four journals have distinct scopes:

1. State `write.commit` makes staged record changes or delete tombstones
   authoritative and rolls committed work forward after a crash. Delete replay
   removes the compatibility record before the authoritative record, then
   retires the journal; every step is idempotent. Checkpoint and restore refuse
   to proceed while committed journal work awaits recovery.
2. State `restore.commit` atomically replaces the state tree and discards or
   completes interrupted staging.
3. Whole-storage `.restore.commit` coordinates package, database, security,
   state, and staged-content restore into a clean target.
4. `publication-journal.json` records publication intent and terminal outcomes
   so an idempotent retry cannot publish twice.

State commit journals are control files and are excluded from backups. The
publication journal is authoritative staged-publication recovery state and is
included. [Storage backup
tests](../../tests/NuExtVault.UnitTests/StorageBackupTests.cs) exercise the
capture/restore matrix, archive smuggling, interruption points, and bounded
streaming.

The implementation is per-process, single-node, and filesystem-based. It has no
distributed transaction, public custom backup participant, or online snapshot.

---

[Contributor manual](README.md) | **Previous:** [Capabilities and security](04-capabilities-and-security.md) | **Next:** [Public SDK and trusted loading](06-public-sdk-and-trusted-loading.md)

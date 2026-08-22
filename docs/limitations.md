# Important limitations

- This is test infrastructure, not a production package feed.
- Programmatic test-server storage is in memory; the CLI persists packages locally.
- The server uses anonymous HTTP by default unless credentials are supplied.
- The default policy scanner performs structural policy checks, not antivirus;
  real malware detection requires an injected `IPackagePolicyScanner`.
- Signature validation checks NuGet signature/content integrity but does not
  configure signer allow-lists or guarantee online revocation checks.
- Automatic certificate provisioning, advanced network faults, symbol download,
  and repository signatures are not yet implemented.
- The server binds to `127.0.0.1` unless its hosting configuration is changed.
- Extension assembly-load contexts provide dependency and shutdown isolation, not a
  security sandbox. Installing, updating, enabling, disabling, or unloading an
  extension requires a restart.
- Performance targets documented for indexed storage are CI regression budgets, not
  service-level guarantees.

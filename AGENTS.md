# Agent Instructions

These instructions apply to the entire repository.

## Design documents

- Create design documents in `.design/<topic>.md` by default, using a
  descriptive topic filename.
- Treat `.design/` as temporary, local storage. Keep a design there unless the
  user explicitly asks to publish or move it into source control.
- When the user asks to publish or move a design, move the file from `.design/`
  to the tracked `design/` directory.

## Required development workflow

For every feature, behavioral change, protocol change, or bug fix:

1. **Propose first.**
   - Inspect the existing implementation and tests.
   - Describe the intended behavior, affected APIs, compatibility implications,
     security considerations, and test plan.
   - Do not modify production code or tests during this phase.

2. **Wait for explicit user confirmation.**
   - Do not infer approval from the original request.
   - Revise the proposal when requested.
   - Begin implementation only after the user clearly approves it.

3. **Write tests before implementation.**
   - Add or update unit tests for isolated behavior.
   - Add functional or end-to-end tests for externally observable behavior.
   - Prefer real Kestrel and actual NuGet clients for protocol behavior.
   - Run the new tests and confirm they fail for the expected reason before
     changing production code.

4. **Implement against the tests.**
   - Make the smallest complete production change that satisfies the approved
     proposal.
   - Preserve existing behavior unless the proposal explicitly changes it.
   - Do not weaken, delete, or rewrite valid tests merely to make the
     implementation pass.

5. **Validate the complete change.**
   - Run the targeted tests, then the full unit and functional suites.
   - Build with warnings treated as errors.
   - Validate packaging or CLI behavior when those surfaces change.

6. **Update the README last.**
   - Document the final, implemented commands and behavior.
   - Clearly distinguish implemented features from proposed or deferred work.
   - Keep examples synchronized with the tested CLI and public APIs.

Documentation-only corrections do not require new tests, but still require a
proposal and explicit confirmation when they change documented behavior or
project policy.

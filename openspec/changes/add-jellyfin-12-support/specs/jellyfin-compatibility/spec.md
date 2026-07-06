# jellyfin-compatibility

## ADDED Requirements

### Requirement: The compatibility floor is explicit and only moves deliberately

The Jellyfin SDK version the plugin compiles against SHALL equal `targetAbi.txt` and SHALL be treated as the real minimum server version a release loads on. Raising the floor MUST be a deliberate decision made in a PR that bumps `targetAbi.txt` and the plugin minor version together; dependabot SDK PRs are a compile-signal, not a merge queue.

#### Scenario: Dependabot opens the SDK-12 PR at stable

- **WHEN** Jellyfin 12.0.0 ships and dependabot opens the `Jellyfin.Controller`/`Jellyfin.Model` 12.0.0 bump
- **THEN** the PR is used to read CI's compile/test result against the new ABI and is closed without merge unless a floor raise has been explicitly decided, because merging would drop every 10.11 server (net10.0 assemblies do not load on .NET 9 hosts)

### Requirement: Prerelease compatibility is verified continuously during a major RC window

While a Jellyfin major release is in RC, CI SHALL build the plugin and run the unit test suite against a pinned RC SDK on that major's target framework, as a non-blocking job whose failure produces a visible warning but does not block merges.

#### Scenario: An RC changes ABI under us

- **WHEN** the pinned RC SDK stops compiling or a test fails on the prerelease leg
- **THEN** main CI stays green and shippable, the prerelease leg turns red naming the exact RC, and the breakage is triaged weeks before stable instead of after user reports

### Requirement: Major-version support is declared only after verification on the stable release

The README and site SHALL NOT claim support for a Jellyfin major version until the full test suite passes against that version's stable SDK and a manual smoke test (install from catalog, link, sync, watchlist, diary import) has passed on a stable server.

#### Scenario: 12.0.0 stable ships

- **WHEN** 12.0.0 is published
- **THEN** the prerelease leg is repointed at stable, the suite and smoke test run, and only then does the compatibility statement change from "known to run on 12.0 RC" to "supported"

### Requirement: ABI breaks split the release stream via manifest targetAbi

If the plugin adopts a Jellyfin major SDK whose target framework or ABI excludes older servers, the new line SHALL ship with `targetAbi` set to that major (so older servers are never offered it), the previous line's manifest entries SHALL remain, and the previous floor SHALL receive critical fixes from a maintenance branch until a documented end condition.

#### Scenario: A 10.11 server checks the catalog after the split

- **WHEN** the manifest carries both a 12-floor line and older 10.11-floor versions
- **THEN** a 10.11 server is offered only the last 10.11-floor version and never auto-updates onto an assembly it cannot load

### Requirement: Users get migration guidance that preserves their data

The README SHALL document what a Jellyfin major upgrade means for this plugin, including that configuration and sync history survive a remove-and-reinstall from the catalog, so users following Jellyfin's "remove repository plugins before migrating" advice lose nothing.

#### Scenario: User removes the plugin before migrating to 12

- **WHEN** a user uninstalls the plugin, migrates the server to 12, and reinstalls from the catalog
- **THEN** their accounts, settings, and sync history are intact and no film is re-synced as a duplicate

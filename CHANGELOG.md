# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project uses
[Semantic Versioning](https://semver.org/).

## [Unreleased]

### Added

- `DesktopHost.Logging`: `RollingFileLoggerProvider` — a file log for `Microsoft.Extensions.Logging`: one file per UTC
  day, continued in a numbered part at a size limit (default 64 MiB; a restart on the same day appends to the first part
  that is not full), old files removed by age (default 7 days, optionally over a wider prefix shared by several
  writers) and by count (default the newest 31 of the provider's own files, never the one being written). Lines are
  written whole under one lock and flushed; the file is shared for reading while it grows; entries after `Dispose`
  are dropped. `AddRollingFile(directory, filePrefix, configure)` registers it; the clock is a `TimeProvider`.

## [0.1.0] - 2026-10-17

### Added

- Repository, scope and license.
- `DesktopHost.Files`: `AtomicFile` — atomic text-file replace (temp sibling, flush to disk, POSIX-semantics rename
  on Windows, time-budgeted retry whose final exception names the holding processes) and shared reads that never
  block it, with `AtomicFile.Statistics` counters for diagnosing a refused or gapped replace.
- `DesktopHost.Files`: `FileRetry` — file and directory delete and move that retry through transient locks, with a
  configurable budget (`FileRetryOptions`, default 10 attempts, waits 1 s, 2 s, 3 s … capped at 3 s), and
  best-effort `TryDeleteFile` / `TryDeleteDirectory`.
- `DesktopHost.Files`: `FileLockHolders` — names the processes holding a file open, via the Windows Restart Manager.

[Unreleased]: https://github.com/iyulab/desktop-host/compare/v0.1.0...HEAD
[0.1.0]: https://github.com/iyulab/desktop-host/releases/tag/v0.1.0

# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project uses
[Semantic Versioning](https://semver.org/).

## [Unreleased]

### Added

- Repository, scope and license.
- `DesktopHost.Files`: `AtomicFile` — atomic text-file replace (temp sibling, flush to disk, POSIX-semantics rename
  on Windows, time-budgeted retry whose final exception names the holding processes) and shared reads that never
  block it, with `AtomicFile.Statistics` counters for diagnosing a refused or gapped replace.
- `DesktopHost.Files`: `FileRetry` — file and directory delete and move that retry through transient locks, with a
  configurable budget (`FileRetryOptions`, default 10 attempts, waits 1 s, 2 s, 3 s … capped at 3 s), and
  best-effort `TryDeleteFile` / `TryDeleteDirectory`.
- `DesktopHost.Files`: `FileLockHolders` — names the processes holding a file open, via the Windows Restart Manager.

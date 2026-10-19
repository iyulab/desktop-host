# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project uses
[Semantic Versioning](https://semver.org/).

## [Unreleased]

### Added

- `DesktopHost.Files`: `Trash.Move(path)` — moves a file or folder to the platform's trash so it can be restored. Windows:
  the shell's `IFileOperation` (the operation File Explorer runs) with every dialog suppressed, on a single-threaded
  apartment of its own, through source-generated COM (the package stays AOT-compatible); a progress sink refuses any
  deletion the shell would not recycle, so an item too large for the Recycle Bin, or on a drive without one, stays where
  it is instead of being deleted, and `TrashException` (`Reason` `not_recyclable` or `refused`) says why. Linux: the
  freedesktop.org Trash specification's home trash (`$XDG_DATA_HOME/Trash`, a `.trashinfo` record with the percent-encoded
  original path and the time, a numbered name when one is taken). macOS: `PlatformNotSupportedException` for now.

## [0.2.0] - 2026-10-17

### Added

- `DesktopHost.Logging`: `RollingFileLoggerProvider` — a file log for `Microsoft.Extensions.Logging`: one file per UTC
  day, continued in a numbered part at a size limit (default 64 MiB; a restart on the same day appends to the first part
  that is not full), old files removed by age (default 7 days, optionally over a wider prefix shared by several
  writers) and by count (default the newest 31 of the provider's own files, never the one being written). Lines are
  written whole under one lock and flushed; the file is shared for reading while it grows; entries after `Dispose`
  are dropped. `AddRollingFile(directory, filePrefix, configure)` registers it; the clock is a `TimeProvider`.
- `DesktopHost.Processes`: `ProcessSupervisor` — runs a long-lived child through a ChildProcessGuard `ProcessGuardian`
  (a job object on Windows, a process group elsewhere, so the tree ends with the host; one guardian may serve every
  supervisor of a host and is not disposed when it was passed in). The command is resolved at every start and names the
  child's whole environment (`SupervisedCommand.SafeEnvironment()` to start from; `SplitCommandLine` for a one-string
  command). A `Ready` hook binds the child's input and output and returns when it is ready, within `ReadyTimeout`
  (60 s). An unexpected exit — or a start that never became ready — is retried after each of `RestartPolicy.Delays`
  (2 s, 8 s, 32 s); a run that lasted `StableAfter` (2 min) starts the count over; `IsPermanentExit` gives up at once
  (`ExitCodes.IsUnhandledManagedException` for `0xE0434352`). Stop: the caller's `GracefulStop` step and
  `GracefulTimeout` (5 s), optionally the main window's close, then the guardian's tree kill. A replaced or stopped
  process never schedules a restart. State, `LastError`, restart and unexpected-exit counts, the last standard-error
  lines; `StateChanged`, `Exited`, `GaveUp` and `StderrLine` are raised outside the supervisor's lock.

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

[Unreleased]: https://github.com/iyulab/desktop-host/compare/v0.2.0...HEAD
[0.2.0]: https://github.com/iyulab/desktop-host/compare/v0.1.0...v0.2.0
[0.1.0]: https://github.com/iyulab/desktop-host/releases/tag/v0.1.0

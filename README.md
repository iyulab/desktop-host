# desktop-host

.NET building blocks for the local host process of a desktop app — the long-running .NET process
that does an app's local work (files, models, devices) next to its window.

Such a process ends up writing the same platform code for itself, and gets it wrong in the same
quiet ways:

- **Durable files** — a write that survives a crash or a power cut, the brief refusals of virus
  scanners and sync clients, finding which process holds a file
- **Its own log** — a file log that rotates and never fills the disk
- **The processes it starts** — workers and helpers that start, report ready, restart within
  limits and never outlive it
- **What it runs** — a program it downloaded, checked for its publisher's signature before it is
  started
- **Its own health** — noticing when its thread pool stops starting work, the state in which it
  answers nothing yet looks alive to itself

**Status:** 0.5.0 — `DesktopHost.Files`, `DesktopHost.Logging`, `DesktopHost.Processes`, `DesktopHost.Signatures` and
`DesktopHost.Diagnostics` on NuGet, one version for all; what `main` adds since is under `[Unreleased]` in the
[changelog](CHANGELOG.md).
See [SCOPE.md](SCOPE.md) for what belongs here.

## Packages

### DesktopHost.Diagnostics

What the host can say about its own health.

- `ThreadPoolStallMonitor` — on a thread of its own (so a starved pool cannot starve it), queues one small work item every
  `Interval` and waits for it to start. One that has not started within `Threshold` is a stall: logged with the pool's
  queue and thread count, and logged again with its length when the work item finally runs. `StallStarted` and
  `StallEnded` hand the same facts to the host (`ThreadPoolStallStarted`, `ThreadPoolStall` with the worst
  `ThreadPoolSnapshot` seen), on the monitor's thread.
- Why it matters: past its minimum, the pool adds threads only slowly (about two a second), so a burst of blocking work —
  a synchronous wait on a task, a long synchronous walk on a request thread — delays every queued item behind it, request
  handlers and health endpoints included. Set `Threshold` to what an outside watcher tolerates (a supervisor's health-check
  timeout) and the host says so before it is restarted for being silent.

```csharp
using DesktopHost.Diagnostics;

using var monitor = new ThreadPoolStallMonitor(new ThreadPoolStallMonitorOptions
{
    Threshold = TimeSpan.FromSeconds(5),
    StallEnded = stall => metrics.Record("pool_stall_ms", stall.Duration.TotalMilliseconds),
}, logger);
monitor.Start();
```

### DesktopHost.Files

Durable file writes and the reads that go with them.

- `AtomicFile` — replace a text file so that a reader sees the whole old or the whole new document, never a part:
  temp sibling, flush to disk, one atomic rename (a POSIX-semantics rename on Windows), retried within a two-second
  window (`FileRetry`'s window rule, shorter pauses) through brief locks, with an exception that names who held the file. Shared reads (`ReadAllTextShared`,
  `ReadLinesShared`, and `…IfExists` forms that answer "absent" only for a missing file) never block such a write.
- `FileRetry` — delete or move a file or directory through the seconds a virus scanner or sync client holds it. The
  retry is a window (`FileRetryOptions.Patience`, 25 s by default): a refusal that ends within it is outlasted, the last
  attempt is made when it closes, nothing waits past it. The Tauri kit's file crate retries by the same rule, and both run
  one case table (`tests/DesktopHost.Files.Tests/cases/file-retry.json`).
- `FileLockHolders` — name the processes holding a file open (Windows Restart Manager; empty elsewhere).
- `Trash` — move a file or folder to the Recycle Bin (Windows, the shell's own operation with no dialogs) or the
  freedesktop.org home trash (Linux), so the person can restore it. It never deletes: an item the platform would not keep
  in its bin — too large, a drive without one — stays where it is and `TrashException` says why.

```csharp
using DesktopHost.Files;

// A reader never sees half of this, even while it is being written.
await AtomicFile.WriteAllTextAsync(path, json);

// null only when the file is not there; any other failure throws.
string? text = await AtomicFile.ReadAllTextSharedIfExistsAsync(path);

// Outlasts a scanner holding a just-extracted file.
FileRetry.MoveDirectory(staging, target, logger);

// Restorable from the Recycle Bin; never a permanent delete.
Trash.Move(path);
```

### DesktopHost.Logging

A file log for `Microsoft.Extensions.Logging` that rotates and never fills the disk.

- `RollingFileLoggerProvider` — `{prefix}-{yyyy-MM-dd}.log` per UTC day, continued in `{prefix}-{yyyy-MM-dd}.1.log`, `.2.log`
  … at a size limit; old files removed by age and by count whenever a file is opened. Lines from any thread come out
  whole; another process can read the file as it grows; entries after shutdown are dropped rather than thrown.
- `RollingFileLoggerOptions` — directory and prefix (required), `RetentionPrefix` (clear the old files of several writers
  that share a prefix), `RetainedFileTimeLimit` (7 days), `RetainedFileCountLimit` (31), `FileSizeLimitBytes` (64 MiB),
  `TimeProvider`.

```csharp
using DesktopHost.Logging;

builder.Logging.AddRollingFile(logDirectory, "host");

// Each worker writes its own file; any of them clears the old files of all.
builder.Logging.AddRollingFile(logDirectory, $"worker-{id}", o => o.RetentionPrefix = "worker");
```

### DesktopHost.Processes

Supervision of the long-lived processes a host starts — on top of [ChildProcessGuard](https://www.nuget.org/packages/ChildProcessGuard),
which keeps them from outliving the host.

- `ProcessSupervisor` — start, wait until ready, start again after an unexpected exit within limits, stop the child's
  own way before killing its whole tree. A process replaced on request or stopped never triggers a restart; events are
  raised outside the supervisor's lock, so a handler can call back in.
- `ProcessSupervisorOptions` — `Command` (resolved at each start; the child gets only the environment it names), `Ready` +
  `ReadyTimeout`, `RestartPolicy` (`Delays`, `StableAfter`, `IsPermanentExit`), `StopPolicy` (`GracefulStop`,
  `GracefulTimeout`, `CloseMainWindow`), `LivenessPolicy` (below), `Guardian` (share one per host), `Logger`, `StderrTailLines`.
- `LivenessPolicy` — for a child that can be alive and still not answer: your check (`Probe` — an HTTP path, a ping on a
  pipe) every `Period`, a miss when it says no, throws or takes longer than `Timeout`; `FailureThreshold` misses in a row
  (default 3; any answer starts the count over, misses inside `StartupGrace` after ready are not counted) stop the child as a
  failed run whose reason names the checks, and the restart policy takes it from there.
- The words differ from a shell kit's on purpose. `RestartPolicy` here is a ladder of growing pauses (2 s, 8 s, 32 s) that
  starts over after a run lasting `StableAfter` — the policy for a host's own workers. A shell supervising the host itself
  (the Tauri kit's sidecar `RestartPolicy`) allows a number of starts within a window and then stays down, with no growing
  pause and no reset; an app using both should not read «restarts» as one rule.

```csharp
using DesktopHost.Processes;

var env = SupervisedCommand.SafeEnvironment();
env["WORKER_PORT"] = port.ToString();

await using var worker = new ProcessSupervisor(new ProcessSupervisorOptions
{
    Name = "worker",
    Guardian = guardian, // one per host
    Command = () => new SupervisedCommand(workerPath, ["--serve"], env),
    Ready = async (process, ct) => await WaitForHelloAsync(process.StandardOutput, ct),
    Stop = { GracefulStop = (process, ct) => process.StandardInput.WriteLineAsync("quit".AsMemory(), ct) },
});
await worker.StartAsync();
```

### DesktopHost.Signatures

Whether a file the host is about to run — an update it downloaded, a helper, a plugin's program — comes from the
publisher it expects.

- `Authenticode.Verify(path, publishers)` — checks the file's embedded Authenticode signature the way Windows checks a
  program (`WinVerifyTrust`: the content unchanged since signing, a chain to a trusted root, a certificate issued for code
  signing and valid when signed), then matches the subject of the certificate that check verified against the expected
  publishers. `SignatureCheck.Verdict`: `Trusted`, `NotSigned`, `Invalid`, `UnexpectedPublisher`, or `Unsupported` off
  Windows (nothing checked — the caller decides what that means there).
- `Publisher` — names a publisher by its certificate subject: the organization (`O`), and optionally the country, the
  registration number an extended-validation certificate carries (`serialNumber`) and the common name — each compared
  exactly. A subject survives a certificate's renewal, so a pin on it keeps passing when the certificate (and its
  thumbprint) is replaced; a thumbprint pin would fail on the first file signed after a renewal.
- Offline by design: revocation is not checked and nothing is fetched to build the chain, so a computer without a
  connection still verifies what it downloaded through another path. A certificate revoked after a file was signed still
  passes this check.
- `FileCatalog.Verify(directory, catalog, publishers)` — the files beside a signed program: a directory checked against a
  signed Windows catalog (`.cat`, made by PowerShell's `New-FileCatalog -CatalogVersion 2.0` and signed like a program).
  The catalog's own signature goes through the same check and publishers; then every file under the directory must be
  listed with the hash Windows computes for it (a program's Authenticode hash, any other file's SHA-256), and every listed
  file must be there. `CatalogCheck.Verdict`: `Trusted`, `CatalogNotTrusted`, `CatalogUnreadable`, `FileNotListed`,
  `FileChanged`, `FileMissing`, or `Unsupported` off Windows; `File` names the file a verdict is about. The catalog may sit
  inside the directory it lists.

```csharp
using DesktopHost.Signatures;

var publisher = new Publisher("Example Software Ltd") { Country = "US", SerialNumber = "1234567" };
var check = Authenticode.Verify(downloadedExe, [publisher]);
if (!check.IsTrusted)
    throw new InvalidOperationException($"refusing {downloadedExe}: {check}");

var staged = FileCatalog.Verify(stagedDirectory, Path.Combine(stagedDirectory, "component.cat"), [publisher]);
if (!staged.IsTrusted)
    throw new InvalidOperationException($"refusing {stagedDirectory}: {staged}");
```

## License

MIT — see [LICENSE](LICENSE).

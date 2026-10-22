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

**Status:** 0.3.0 — `DesktopHost.Files`, `DesktopHost.Logging` and `DesktopHost.Processes` on NuGet;
`DesktopHost.Signatures` is unreleased (on `main`).
See [SCOPE.md](SCOPE.md) for what belongs here.

## Packages

### DesktopHost.Files

Durable file writes and the reads that go with them.

- `AtomicFile` — replace a text file so that a reader sees the whole old or the whole new document, never a part:
  temp sibling, flush to disk, one atomic rename (a POSIX-semantics rename on Windows), retried for about two seconds
  through brief locks, with an exception that names who held the file. Shared reads (`ReadAllTextShared`,
  `ReadLinesShared`, and `…IfExists` forms that answer "absent" only for a missing file) never block such a write.
- `FileRetry` — delete or move a file or directory through the seconds a virus scanner or sync client holds it.
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
  `GracefulTimeout`, `CloseMainWindow`), `Guardian` (share one per host), `Logger`, `StderrTailLines`.

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

```csharp
using DesktopHost.Signatures;

var publisher = new Publisher("Example Software Ltd") { Country = "US", SerialNumber = "1234567" };
var check = Authenticode.Verify(downloadedExe, [publisher]);
if (!check.IsTrusted)
    throw new InvalidOperationException($"refusing {downloadedExe}: {check}");
```

## License

MIT — see [LICENSE](LICENSE).

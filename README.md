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

**Status:** no package is published yet; the first one, `DesktopHost.Files`, is in the repository at 0.1.0.
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

```csharp
using DesktopHost.Files;

// A reader never sees half of this, even while it is being written.
await AtomicFile.WriteAllTextAsync(path, json);

// null only when the file is not there; any other failure throws.
string? text = await AtomicFile.ReadAllTextSharedIfExistsAsync(path);

// Outlasts a scanner holding a just-extracted file.
FileRetry.MoveDirectory(staging, target, logger);
```

## License

MIT — see [LICENSE](LICENSE).

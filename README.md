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

**Status:** no package is published yet. See [SCOPE.md](SCOPE.md) for what belongs here.

## License

MIT — see [LICENSE](LICENSE).

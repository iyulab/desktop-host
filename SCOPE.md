# Scope

desktop-host holds the .NET building blocks a desktop app's local host process ends up writing
for itself — the parts that run on the user's computer inside that process. This document draws
the line around that job.

## What it owns

**Platform behaviour a long-running local .NET process needs and gets wrong when it writes it
alone:**

- **Files** — writes that survive a crash, retries through the brief locks of scanners and sync
  clients, telling which process holds a file
- **Logging** — a rotating file log for `Microsoft.Extensions.Logging`, bounded in size and count
- **Child processes** — starting workers and helpers, waiting until they are ready, restarting
  them within a limit, and stopping the whole tree, including on Windows when the host itself dies

One capability per package, so a host takes only what it needs. All packages share one version.

## What it does not do

- **Anything that must agree with a desktop shell's own code.** When a .NET piece and a shell
  written in another language have to share a format or a rule — a credential entry, an error
  report, the handshake between a shell and its helper — both halves live together in the shell
  kit that owns that agreement (for Tauri apps, `tauri-kit` and its `dotnet/` packages). This
  repository holds what a .NET host needs on its own.
- **Know its callers.** No code path, option, default, comment, test fixture or commit message
  assumes a particular consuming app. Folder names, environment variable names and wording arrive
  as arguments.
- **Model a domain.** What an app's files and processes mean is the app's.
- **Web framework or interface.** HTTP routes, UI and design tokens belong elsewhere.

## Admission

A capability belongs here when it would be written the same way for an unrelated desktop app's
.NET host on the same platform, and can be stated without naming a caller — every constant it
needs becomes a parameter.

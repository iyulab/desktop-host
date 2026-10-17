using System.Text;

namespace DesktopHost.Processes;

/// <summary>What a <see cref="ProcessSupervisor"/> runs: a program, its arguments, its whole environment, and where.</summary>
/// <param name="FileName">The program to start.</param>
/// <param name="Arguments">Its arguments, one per element — never re-split or re-quoted.</param>
/// <param name="Environment">
/// The child's entire environment. Nothing from this process is inherited unless it is listed here; start from
/// <see cref="SafeEnvironment"/> and add what the child needs.
/// </param>
/// <param name="WorkingDirectory">The child's working directory; <c>null</c> for this process's own.</param>
public sealed record SupervisedCommand(
    string FileName,
    IReadOnlyList<string> Arguments,
    IReadOnlyDictionary<string, string> Environment,
    string? WorkingDirectory = null)
{
    /// <summary>
    /// The variables a child may inherit without asking: where to find programs and native libraries, a temp
    /// directory, the user's profile folders, the locale, and where the .NET runtime lives when it is not at its
    /// default location. Paths and settings, never credentials.
    /// </summary>
    public static IReadOnlyList<string> SafeEnvironmentVariables { get; } =
    [
        "PATH", "PATHEXT",
        "SYSTEMROOT", "SYSTEMDRIVE", "WINDIR", "COMSPEC",
        "USERPROFILE", "HOMEDRIVE", "HOMEPATH", "HOME",
        "TEMP", "TMP", "TMPDIR",
        "PROGRAMFILES", "PROGRAMFILES(X86)", "PROGRAMDATA",
        "APPDATA", "LOCALAPPDATA",
        "LANG", "LC_ALL",
        "DOTNET_ROOT", "DOTNET_ROOT_X64", "DOTNET_ROOT_ARM64", "DOTNET_HOST_PATH",
    ];

    /// <summary>
    /// A new, case-insensitive dictionary holding the <see cref="SafeEnvironmentVariables"/> this process has set —
    /// the starting point for a <see cref="SupervisedCommand.Environment"/>.
    /// </summary>
    public static Dictionary<string, string> SafeEnvironment()
    {
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in SafeEnvironmentVariables)
        {
            if (System.Environment.GetEnvironmentVariable(name) is { } value) env[name] = value;
        }
        return env;
    }

    /// <summary>
    /// Splits a command line on whitespace, keeping a double-quoted segment together and dropping the quotes — for a
    /// command given as one string (a setting, an environment variable).
    /// </summary>
    public static IReadOnlyList<string> SplitCommandLine(string commandLine)
    {
        ArgumentNullException.ThrowIfNull(commandLine);
        var parts = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;
        var hasToken = false;
        foreach (var c in commandLine)
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
                hasToken = true;
                continue;
            }
            if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (hasToken)
                {
                    parts.Add(current.ToString());
                    current.Clear();
                    hasToken = false;
                }
                continue;
            }
            current.Append(c);
            hasToken = true;
        }
        if (hasToken) parts.Add(current.ToString());
        return parts;
    }
}

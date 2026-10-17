using Microsoft.Extensions.Logging;

namespace DesktopHost.Logging;

/// <summary>Registers a <see cref="RollingFileLoggerProvider"/> on an <see cref="ILoggingBuilder"/>.</summary>
public static class RollingFileLoggingBuilderExtensions
{
    /// <summary>
    /// Adds a rotating file log writing <c>{filePrefix}-{yyyy-MM-dd}.log</c> files under <paramref name="directory"/>.
    /// </summary>
    /// <param name="builder">The logging builder.</param>
    /// <param name="directory">The directory the files are written to.</param>
    /// <param name="filePrefix">The start of every file name, e.g. <c>host</c>.</param>
    /// <param name="configure">Adjusts the remaining options (retention, size limit, clock).</param>
    public static ILoggingBuilder AddRollingFile(this ILoggingBuilder builder, string directory, string filePrefix,
        Action<RollingFileLoggerOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var options = new RollingFileLoggerOptions { Directory = directory, FilePrefix = filePrefix };
        configure?.Invoke(options);
        builder.AddProvider(new RollingFileLoggerProvider(options));
        return builder;
    }
}

using System;
using System.Globalization;
using System.IO;
using System.Text;
using Microsoft.Extensions.Logging;

namespace DbManager.Services.Logging
{
    /// <summary>
    /// Dependency-free file logging: one plain-text line per log event, appended
    /// to a daily file (db-manager-yyyy-MM-dd.txt) under the configured directory.
    /// Only Warning and above are written — the app logs real errors, not trace
    /// noise — and exceptions are written verbosely (the full ToString, so type,
    /// message and stack all land in the file). Errors happen on a background
    /// circuit thread with no console, so this file is the only durable trace.
    ///
    /// NOTE: this file contains exception detail and stack traces, and provider
    /// exceptions can embed host/user information. Treat it as sensitive and keep
    /// its directory access-restricted; files older than <see cref="RetentionDays"/>
    /// are pruned automatically so old traces do not pile up on disk.
    /// </summary>
    public sealed class FileLoggerProvider : ILoggerProvider
    {
        /// <summary>Daily files older than this are deleted on the next write of a new day.</summary>
        public const int RetentionDays = 30;

        private readonly string _directory;
        private readonly string _filePrefix;
        private readonly LogLevel _minLevel;

        // Set under the FileLogger write lock; guards the once-per-day prune.
        internal DateTime LastPruneDate = DateTime.MinValue;

        public FileLoggerProvider(string directory, string filePrefix = "db-manager", LogLevel minLevel = LogLevel.Warning)
        {
            _directory = directory;
            _filePrefix = filePrefix;
            _minLevel = minLevel;
        }

        public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

        public void Dispose() { }

        internal string Directory => _directory;
        internal string FilePrefix => _filePrefix;
        internal LogLevel MinLevel => _minLevel;

        /// <summary>
        /// Deletes daily files older than <see cref="RetentionDays"/>. Called at most
        /// once per day, under the logger's write lock. Never throws: a failed prune
        /// must not take logging (or the app) down.
        /// </summary>
        internal void PruneIfDue()
        {
            var today = DateTime.Now.Date;
            if (LastPruneDate == today)
                return;
            LastPruneDate = today;

            try
            {
                if (!System.IO.Directory.Exists(_directory))
                    return;

                var suffixLength = $"{_filePrefix}-".Length;
                foreach (var path in System.IO.Directory.EnumerateFiles(_directory, $"{_filePrefix}-*.txt"))
                {
                    var name = Path.GetFileNameWithoutExtension(path);
                    if (name.Length != suffixLength + 10)
                        continue;
                    if (!DateTime.TryParseExact(name.Substring(suffixLength), "yyyy-MM-dd",
                            CultureInfo.InvariantCulture, DateTimeStyles.None, out var fileDate))
                        continue;
                    if ((today - fileDate).TotalDays > RetentionDays)
                        File.Delete(path);
                }
            }
            catch
            {
                // Pruning is best-effort.
            }
        }
    }

    /// <summary>
    /// The per-category logger. Writes are serialized on a shared lock (the
    /// Blazor Server host logs from many threads) and IO failures are swallowed:
    /// logging must never take the app down, so a locked/full disk just means no
    /// line, not a crashed circuit.
    /// </summary>
    internal sealed class FileLogger : ILogger
    {
        private static readonly object WriteLock = new();

        private readonly FileLoggerProvider _provider;
        private readonly string _category;

        public FileLogger(FileLoggerProvider provider, string category)
        {
            _provider = provider;
            _category = category;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) =>
            logLevel != LogLevel.None && logLevel >= _provider.MinLevel;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
                return;

            var line = Format(logLevel, formatter(state, exception), exception);

            try
            {
                lock (WriteLock)
                {
                    Directory.CreateDirectory(_provider.Directory);
                    _provider.PruneIfDue();
                    File.AppendAllText(PathFor(DateTimeOffset.Now), line);
                }
            }
            catch
            {
                // Never throw from a logger.
            }
        }

        private string PathFor(DateTimeOffset when) =>
            Path.Combine(_provider.Directory, $"{_provider.FilePrefix}-{when:yyyy-MM-dd}.txt");

        private string Format(LogLevel level, string message, Exception? exception)
        {
            var sb = new StringBuilder();
            sb.Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz"));
            sb.Append(" [").Append(Level(level)).Append("] ");
            sb.Append(_category).Append(": ").AppendLine(message);
            // Full ToString, not just Message: the stack is the point of "verbose".
            if (exception is not null)
                sb.AppendLine(exception.ToString());
            return sb.ToString();
        }

        private static string Level(LogLevel level) => level switch
        {
            LogLevel.Trace => "TRC",
            LogLevel.Debug => "DBG",
            LogLevel.Information => "INF",
            LogLevel.Warning => "WRN",
            LogLevel.Error => "ERR",
            LogLevel.Critical => "CRT",
            _ => "?"
        };
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace LabelPrinter.Core
{
    public enum LogLevel
    {
        Info,
        Success,
        Warn,
        Error
    }

    public sealed class LogEntry
    {
        public DateTime Time { get; set; }
        public LogLevel Level { get; set; }
        public string Message { get; set; }

        public string LevelText
        {
            get
            {
                switch (Level)
                {
                    case LogLevel.Success: return "OK ";
                    case LogLevel.Warn: return "ВНИМ ";
                    case LogLevel.Error: return "ОШИБКА";
                    default: return "инфо";
                }
            }
        }

        /// <summary>Строка для вывода в окно журнала.</summary>
        public string Line
        {
            get
            {
                return Time.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture)
                       + "  [" + LevelText + "]  " + Message;
            }
        }
    }

    /// <summary>
    /// Журнал приложения: кольцевой буфер для окна + ежемесячный файл на диске.
    /// Потокобезопасен — вызывается из фоновых задач печати.
    /// </summary>
    public static class Log
    {
        private const int MaxEntries = 5000;

        private static readonly object Gate = new object();
        private static readonly Queue<LogEntry> Entries = new Queue<LogEntry>();

        /// <summary>Срабатывает при добавлении записи. UI подписывается на свой поток.</summary>
        public static event EventHandler<LogEntry> Appended;

        public static IReadOnlyList<LogEntry> Snapshot()
        {
            lock (Gate)
            {
                return new List<LogEntry>(Entries);
            }
        }

        public static void Clear()
        {
            lock (Gate) Entries.Clear();
        }

        public static void Info(string message) { Write(LogLevel.Info, message); }

        public static void Success(string message) { Write(LogLevel.Success, message); }

        public static void Warn(string message) { Write(LogLevel.Warn, message); }

        public static void Error(string message) { Write(LogLevel.Error, message); }

        public static void Error(string message, Exception ex)
        {
            Write(LogLevel.Error, message + " — " + (ex == null ? "?" : ex.GetType().Name + ": " + ex.Message));
        }

        private static void Write(LogLevel level, string message)
        {
            var entry = new LogEntry
            {
                Time = DateTime.Now,
                Level = level,
                Message = message ?? string.Empty
            };

            lock (Gate)
            {
                Entries.Enqueue(entry);
                while (Entries.Count > MaxEntries) Entries.Dequeue();

                try
                {
                    AppPaths.EnsureCreated();
                    File.AppendAllText(
                        AppPaths.LogFile,
                        entry.Time.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)
                        + " [" + entry.LevelText + "] " + entry.Message + Environment.NewLine,
                        Encoding.UTF8);
                }
                catch
                {
                    // Недоступный диск не должен ронять печать.
                }
            }

            EventHandler<LogEntry> handler = Appended;
            if (handler != null) handler(null, entry);
        }
    }
}

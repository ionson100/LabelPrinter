using System;

namespace LabelPrinter.Core.Diagnostics
{
    /// <summary>Куда уходят записи журнала.</summary>
    public interface ILogSink
    {
        void Write(LogMessage message);
    }

    /// <summary>Ничего не пишет. Удобно для библиотечных тестов и сервисов.</summary>
    public sealed class NullLogSink : ILogSink
    {
        public void Write(LogMessage message) { }
    }

    /// <summary>Перенаправляет записи в делегат — мост к вашему логгеру.</summary>
    public sealed class DelegateLogSink : ILogSink
    {
        private readonly Action<LogMessage> _write;

        public DelegateLogSink(Action<LogMessage> write)
        {
            if (write == null) throw new ArgumentNullException("write");
            _write = write;
        }

        public void Write(LogMessage message)
        {
            if (message != null) _write(message);
        }
    }

    /// <summary>Пишет в файл с ротацией по месяцам.</summary>
    public sealed class FileLogSink : ILogSink
    {
        private readonly string _directory;
        private readonly object _gate = new object();
        private DateTime _month;

        public FileLogSink(string directory)
        {
            _directory = directory;
            _month = DateTime.Now;
        }

        public string FilePath
        {
            get { return System.IO.Path.Combine(_directory, "labelprinter-" + _month.ToString("yyyy-MM") + ".log"); }
        }

        public void Write(LogMessage message)
        {
            if (message == null) return;

            try
            {
                lock (_gate)
                {
                    if (message.Time.Year != _month.Year || message.Time.Month != _month.Month)
                    {
                        _month = message.Time;
                    }

                    if (!System.IO.Directory.Exists(_directory))
                    {
                        System.IO.Directory.CreateDirectory(_directory);
                    }

                    System.IO.File.AppendAllText(
                        FilePath,
                        message.Time.ToString("yyyy-MM-dd HH:mm:ss.fff")
                        + " [" + message.LevelText + "] " + message.Message + Environment.NewLine,
                        System.Text.Encoding.UTF8);
                }
            }
            catch
            {
                // Недоступный диск не должен ронять печать.
            }
        }
    }

    /// <summary>Держит последние записи в памяти — удобно для показа при старте.</summary>
    public sealed class MemoryLogSink : ILogSink
    {
        private readonly int _capacity;
        private readonly System.Collections.Generic.Queue<LogMessage> _items =
            new System.Collections.Generic.Queue<LogMessage>();
        private readonly object _gate = new object();

        public MemoryLogSink(int capacity)
        {
            _capacity = capacity < 1 ? 5000 : capacity;
        }

        public void Write(LogMessage message)
        {
            if (message == null) return;
            lock (_gate)
            {
                _items.Enqueue(message);
                while (_items.Count > _capacity) _items.Dequeue();
            }
        }

        public System.Collections.Generic.IReadOnlyList<LogMessage> Snapshot()
        {
            lock (_gate)
            {
                return new System.Collections.Generic.List<LogMessage>(_items);
            }
        }
    }
}
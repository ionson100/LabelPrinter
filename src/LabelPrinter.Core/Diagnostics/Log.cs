using System;

namespace LabelPrinter.Core.Diagnostics
{
    /// <summary>
    /// Точка входа в журнал библиотеки.
    ///
    /// Сделано фасадом по двум причинам:
    ///  • код движка (принтеры, буфер, команды) пишет в журнал без знания о том,
    ///    кто его читает — иначе пришлось бы тянуть <c>ILog</c> через каждый метод;
    ///  • приложение подставляет свой приёмник одной строкой и не получает
    ///    сообщений в <c>Console</c> там, где они не нужны.
    ///
    /// Пример подключения в своём приложении:
    /// <code>
    /// Log.Sink = new FileLogSink(@"C:\logs\labels");
    /// Log.Appended += (s, e) =&gt; Console.WriteLine(e.Line);
    /// </code>
    /// </summary>
    public static class Log
    {
        private static ILogSink _sink = new NullLogSink();

        /// <summary>Приёмник записей. По умолчанию ничего не пишется.</summary>
        public static ILogSink Sink
        {
            get { return _sink; }
            set { _sink = value ?? new NullLogSink(); }
        }

        /// <summary>Срабатывает при каждой записи. Можно подписаться из интерфейса.</summary>
        public static event EventHandler<LogMessage> Appended;

        public static void Info(string message) { Write(LogLevel.Info, message); }

        public static void Success(string message) { Write(LogLevel.Success, message); }

        public static void Warn(string message) { Write(LogLevel.Warn, message); }

        public static void Error(string message) { Write(LogLevel.Error, message); }

        public static void Error(string message, Exception ex)
        {
            Write(LogLevel.Error,
                message + " — " + (ex == null ? "?" : ex.GetType().Name + ": " + ex.Message));
        }

        private static void Write(LogLevel level, string message)
        {
            var entry = new LogMessage(level, message);

            try { _sink.Write(entry); }
            catch { /* приёмник не должен ронять печать */ }

            var handler = Appended;
            if (handler != null)
            {
                try { handler(null, entry); }
                catch { /* подписчик не должен ронять печать */ }
            }
        }
    }
}
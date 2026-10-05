using System;

namespace LabelPrinter.Core.Diagnostics
{
    public enum LogLevel
    {
        Info,
        Success,
        Warn,
        Error
    }

    /// <summary>Запись журнала. <see cref="Line"/> готова к выводу в текстовое поле.</summary>
    public sealed class LogMessage
    {
        public DateTime Time { get; set; }
        public LogLevel Level { get; set; }
        public string Message { get; set; }

        public LogMessage()
        {
            Time = DateTime.Now;
            Level = LogLevel.Info;
            Message = string.Empty;
        }

        public LogMessage(LogLevel level, string message)
        {
            Time = DateTime.Now;
            Level = level;
            Message = message ?? string.Empty;
        }

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

        public string Line
        {
            get
            {
                return Time.ToString("HH:mm:ss.fff")
                       + "  [" + LevelText + "]  " + Message;
            }
        }

        public override string ToString() { return Line; }
    }
}
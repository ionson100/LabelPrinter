using System;

namespace LabelPrinter.Core.Configuration
{
    /// <summary>Настройки одного принтера APLINK (MRX72e / HRX).</summary>
    public sealed class PrinterSettings
    {
        /// <summary>Стабильный идентификатор принтера.</summary>
        public string Id { get; set; }

        public string Name { get; set; }

        public string Host { get; set; }

        public int Port { get; set; }

        /// <summary>Принтер участвует в печати.</summary>
        public bool Enabled { get; set; }

        /// <summary>Имя формата этикетки, загруженного в принтер.</summary>
        public string FormatName { get; set; }

        /// <summary>Имя переменной формата, получающей код.</summary>
        public string VariableName { get; set; }

        /// <summary>Номер печатающей головы (zero-based), для многоголовочных принтеров.</summary>
        public int BoardId { get; set; }

        /// <summary>
        /// useCache в SET_PRINTING_FORMAT. Включать только если формат уже
        /// загружался в принтер вручную, иначе принтер вернёт ошибку.
        /// </summary>
        public bool UseFormatCache { get; set; }

        public PrinterSettings()
        {
            Id = Guid.NewGuid().ToString("N");
            Name = "Принтер 1";
            Host = "192.168.1.10";
            Port = 4100;
            Enabled = true;
            FormatName = "demo";
            VariableName = "code";
            BoardId = 0;
            UseFormatCache = false;
        }

        public PrinterSettings(string name, string host, int port)
        {
            Id = Guid.NewGuid().ToString("N");
            Name = name;
            Host = host;
            Port = port;
            Enabled = true;
            FormatName = "demo";
            VariableName = "code";
            BoardId = 0;
            UseFormatCache = false;
        }

        public string Endpoint { get { return Host + ":" + Port; } }

        public PrinterSettings Clone()
        {
            return (PrinterSettings)MemberwiseClone();
        }

        /// <summary>Возвращает описание проблемы или null, если всё в порядке.</summary>
        public string Validate()
        {
            if (string.IsNullOrWhiteSpace(Name)) return "Не задано имя принтера.";
            if (string.IsNullOrWhiteSpace(Host)) return "Не задан IP-адрес принтера.";
            if (Port < 1 || Port > 65535) return "Порт должен быть в диапазоне 1..65535.";
            if (string.IsNullOrWhiteSpace(FormatName)) return "Не задано имя формата этикетки.";
            if (string.IsNullOrWhiteSpace(VariableName)) return "Не задано имя переменной формата.";
            return null;
        }

        public override string ToString()
        {
            return Name + " (" + Endpoint + ")";
        }
    }
}
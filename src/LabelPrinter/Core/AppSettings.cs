using System;
using System.Collections.Generic;

namespace LabelPrinter.Core
{
    /// <summary>Настройки одного принтера APLINK (MRX72e / HRX).</summary>
    public sealed class PrinterSettings
    {
        /// <summary>Стабильный идентификатор: используется в ключе Redis-буфера.</summary>
        public string Id { get; set; }

        public string Name { get; set; }

        public string Host { get; set; }

        public int Port { get; set; }

        /// <summary>Принтер участвует в печати (галочка «активировать»).</summary>
        public bool Enabled { get; set; }

        /// <summary>Имя формата этикетки, загруженного в принтер (по заданию — demo).</summary>
        public string FormatName { get; set; }

        /// <summary>Имя переменной формата, получающей код (по заданию — code).</summary>
        public string VariableName { get; set; }

        /// <summary>Номер печатающей головы (zero-based). Нужен для многоголовочных принтеров.</summary>
        public int BoardId { get; set; }

        /// <summary>
        /// useCache в SET_PRINTING_FORMAT. Включать только если формат уже
        /// загружался в принтер вручную, иначе принтер вернёт ошибку.
        /// </summary>
        public bool UseFormatCache { get; set; }

        public PrinterSettings Clone()
        {
            return (PrinterSettings)MemberwiseClone();
        }

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

        public string Endpoint { get { return Host + ":" + Port; } }

        public string Validate()
        {
            if (string.IsNullOrWhiteSpace(Name)) return "Не задано имя принтера.";
            if (string.IsNullOrWhiteSpace(Host)) return "Не задан IP-адрес принтера.";
            if (Port < 1 || Port > 65535) return "Порт должен быть в диапазоне 1..65535.";
            if (string.IsNullOrWhiteSpace(FormatName)) return "Не задано имя формата этикетки.";
            if (string.IsNullOrWhiteSpace(VariableName)) return "Не задано имя переменной формата.";
            return null;
        }
    }

    /// <summary>Все настройки приложения. Сохраняются в settings.json.</summary>
    public sealed class AppSettings
    {
        // ---- База данных ----
        public string ConnectionString { get; set; }

        /// <summary>Сколько кодов заливать в таблицу при первом запуске.</summary>
        public int InitialCodeCount { get; set; }

        // ---- Redis ----
        public string RedisConnectionString { get; set; }

        /// <summary>Вместимость буфера в Redis (по умолчанию 100).</summary>
        public int BufferSize { get; set; }

        /// <summary>Когда остаток в буфере опускается до этого значения — долить из базы.</summary>
        public int BufferRefillThreshold { get; set; }

        // ---- Принтеры ----
        public List<PrinterSettings> Printers { get; set; }

        // ---- Режим печати ----
        /// <summary>true — тестовый принтер (без сети, интервальная печать).</summary>
        public bool TestPrinterMode { get; set; }

        /// <summary>Интервал печати в тестовом режиме, секунд (по заданию — 5).</summary>
        public int TestPrintIntervalSeconds { get; set; }

        /// <summary>
        /// Как часто опрашивать счётчик напечатанных этикеток, мс.
        /// Это же время ожидания перед отправкой следующего кода, поэтому
        /// значение равно максимальному темпу печати: 100 мс ≈ 10 этикеток/с.
        /// </summary>
        public int PollIntervalMs { get; set; }

        /// <summary>Пауза перед повторной попыткой подключения, секунд.</summary>
        public int ReconnectDelaySeconds { get; set; }

        /// <summary>Таймаут обмена с принтером, мс.</summary>
        public int PrinterTimeoutMs { get; set; }

        /// <summary>Триггер печати: photocell | distance | timer.</summary>
        public string TriggerMode { get; set; }

        // ---- Генерация кодов ----
        /// <summary>Фиксированная часть AI(01) — GTIN-14.</summary>
        public string Gtin14 { get; set; }

        /// <summary>Алфавит случайной части кода.</summary>
        public string CodeAlphabet { get; set; }

        /// <summary>
        /// Шаблон кода. Плейсхолдеры: {GTIN} — фиксированный GTIN-14,
        /// {R1} и {R2} — случайные блоки длиной RandomPart1Length / RandomPart2Length,
        /// {GS} — разделитель групп 0x1D между элементами GS1.
        /// </summary>
        public string CodeTemplate { get; set; }

        /// <summary>
        /// Передавать разделитель групп как XML-сущность &amp;#x1D; вместо сырого
        /// байта 0x1D. Нужно только если принтер отвергает управляющий символ внутри
        /// атрибута (формально XML 1.0 такое значение не допускает).
        /// </summary>
        public bool GroupSeparatorAsEntity { get; set; }

        /// <summary>
        /// Шаблон, под который в последний раз наполнялась база. Нужен, чтобы
        /// предупредить о смене формата: уже залитые коды останутся в старом виде.
        /// </summary>
        public string LastCodeTemplate { get; set; }

        public int RandomPart1Length { get; set; }

        public int RandomPart2Length { get; set; }

        public AppSettings()
        {
            ConnectionString = "Server=localhost;Port=5432;Database=printer;User Id=postgres;Password=postgres;";
            InitialCodeCount = 10000;

            RedisConnectionString = "localhost:6379,abortConnect=false,connectTimeout=3000,syncTimeout=3000";
            BufferSize = 100;
            BufferRefillThreshold = 10;

            Printers = new List<PrinterSettings>();

            TestPrinterMode = false;
            TestPrintIntervalSeconds = 5;
            PollIntervalMs = 100;
            ReconnectDelaySeconds = 5;
            PrinterTimeoutMs = 5000;
            TriggerMode = "photocell";

            Gtin14 = "02345678987654";
            CodeAlphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";
            CodeTemplate = "01{GTIN}21{R1}{GS}93{R2}";
            RandomPart1Length = 6;
            RandomPart2Length = 4;
            GroupSeparatorAsEntity = false;
        }

        /// <summary>Нормализует значения, приводимые вручную, к рабочим диапазонам.</summary>
        public void Normalize()
        {
            if (InitialCodeCount <= 0) InitialCodeCount = 10000;
            if (BufferSize < 1) BufferSize = 100;
            if (BufferRefillThreshold < 0) BufferRefillThreshold = 0;
            if (BufferRefillThreshold > BufferSize) BufferRefillThreshold = BufferSize / 2;
            if (TestPrintIntervalSeconds < 1) TestPrintIntervalSeconds = 5;
            if (PollIntervalMs < 100) PollIntervalMs = 100;
            if (ReconnectDelaySeconds < 1) ReconnectDelaySeconds = 5;
            if (PrinterTimeoutMs < 200) PrinterTimeoutMs = 200;
            if (string.IsNullOrWhiteSpace(CodeTemplate)) CodeTemplate = "01{GTIN}21{R1}{GS}93{R2}";
            if (string.IsNullOrWhiteSpace(CodeAlphabet)) CodeAlphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";
            if (RandomPart1Length < 0) RandomPart1Length = 0;
            if (RandomPart2Length < 0) RandomPart2Length = 0;
            if (string.IsNullOrWhiteSpace(Gtin14)) Gtin14 = "02345678987654";
            if (string.IsNullOrWhiteSpace(TriggerMode)) TriggerMode = "photocell";
            if (Printers == null) Printers = new List<PrinterSettings>();

            foreach (var p in Printers)
            {
                if (string.IsNullOrWhiteSpace(p.Id)) p.Id = Guid.NewGuid().ToString("N");
                if (string.IsNullOrWhiteSpace(p.FormatName)) p.FormatName = "demo";
                if (string.IsNullOrWhiteSpace(p.VariableName)) p.VariableName = "code";
                if (p.Port < 1 || p.Port > 65535) p.Port = 4100;
            }
        }

        /// <summary>Ключ Redis-буфера для принтера.</summary>
        public string BufferKey(string printerId)
        {
            return "labelprinter:buffer:" + printerId;
        }

        /// <summary>Ключ Redis со счётчиком выданных кодов (для диагностики).</summary>
        public string IssuedKey(string printerId)
        {
            return "labelprinter:issued:" + printerId;
        }
    }
}

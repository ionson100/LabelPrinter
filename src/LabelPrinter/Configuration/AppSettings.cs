using System;
using System.Collections.Generic;
using LabelPrinter.Core.Configuration;
using Newtonsoft.Json;

namespace LabelPrinter.Configuration
{
    /// <summary>
    /// Настройки приложения LabelPrinter.
    ///
    /// Здесь только то, что относится к этому конкретному решению: подключение
    /// к базе и Redis, сколько кодов держать. Настройки принтеров, формата кода и
    /// режима работы живут в библиотеке LabelPrinter.Core и хранятся здесь
    /// «плоскими» полями, чтобы settings.json не менялся по форме.
    /// </summary>
    public sealed class AppSettings
    {
        // ---- База данных ----

        public string ConnectionString { get; set; }

        /// <summary>Сколько кодов заливать в таблицу при первом запуске.</summary>
        public int InitialCodeCount { get; set; }

        // ---- Redis ----

        public string RedisConnectionString { get; set; }

        // ---- Принтеры ----

        public List<PrinterSettings> Printers { get; set; }

        // ---- Настройки библиотеки, разложенные в JSON ----

        public string Gtin14 { get { return CodeFormat.Gtin14; } set { CodeFormat.Gtin14 = value; } }

        public string CodeTemplate { get { return CodeFormat.CodeTemplate; } set { CodeFormat.CodeTemplate = value; } }

        public string CodeAlphabet { get { return CodeFormat.CodeAlphabet; } set { CodeFormat.CodeAlphabet = value; } }

        public int RandomPart1Length { get { return CodeFormat.RandomPart1Length; } set { CodeFormat.RandomPart1Length = value; } }

        public int RandomPart2Length { get { return CodeFormat.RandomPart2Length; } set { CodeFormat.RandomPart2Length = value; } }

        public bool GroupSeparatorAsEntity
        {
            get { return CodeFormat.GroupSeparatorAsEntity; }
            set { CodeFormat.GroupSeparatorAsEntity = value; }
        }

        public int PollIntervalMs { get { return Runtime.PollIntervalMs; } set { Runtime.PollIntervalMs = value; } }

        public int ReconnectDelaySeconds { get { return Runtime.ReconnectDelaySeconds; } set { Runtime.ReconnectDelaySeconds = value; } }

        public int PrinterTimeoutMs { get { return Runtime.PrinterTimeoutMs; } set { Runtime.PrinterTimeoutMs = value; } }

        public string TriggerMode { get { return Runtime.TriggerMode; } set { Runtime.TriggerMode = value; } }

        public int BufferSize { get { return Runtime.BufferSize; } set { Runtime.BufferSize = value; } }

        public int BufferRefillThreshold { get { return Runtime.BufferRefillThreshold; } set { Runtime.BufferRefillThreshold = value; } }

        public bool TestPrinterMode { get { return Runtime.TestPrinterMode; } set { Runtime.TestPrinterMode = value; } }

        public int TestPrintIntervalSeconds { get { return Runtime.TestPrintIntervalSeconds; } set { Runtime.TestPrintIntervalSeconds = value; } }

        /// <summary>
        /// Шаблон, под который в последний раз наполнялась база. Нужен, чтобы
        /// предупредить о смене формата: уже залитые коды останутся в старом виде.
        /// </summary>
        public string LastCodeTemplate { get; set; }

        /// <summary>Настройки формата кода — экземпляр из библиотеки.</summary>
        [JsonIgnore]
        public CodeFormatSettings CodeFormat { get; private set; }

        /// <summary>Настройки режима работы — экземпляр из библиотеки.</summary>
        [JsonIgnore]
        public PrintRuntimeSettings Runtime { get; private set; }

        public AppSettings()
        {
            ConnectionString = "Server=localhost;Port=5432;Database=printer;User Id=postgres;Password=postgres;";
            InitialCodeCount = 10000;
            RedisConnectionString = "localhost:6379,abortConnect=false,connectTimeout=3000,syncTimeout=3000";
            Printers = new List<PrinterSettings>();

            CodeFormat = new CodeFormatSettings();
            Runtime = new PrintRuntimeSettings();
        }

        /// <summary>Создаёт настройки из готовых объектов библиотеки.</summary>
        public AppSettings(CodeFormatSettings codeFormat, PrintRuntimeSettings runtime)
        {
            ConnectionString = "Server=localhost;Port=5432;Database=printer;User Id=postgres;Password=postgres;";
            InitialCodeCount = 10000;
            RedisConnectionString = "localhost:6379,abortConnect=false,connectTimeout=3000,syncTimeout=3000";
            Printers = new List<PrinterSettings>();

            CodeFormat = codeFormat ?? new CodeFormatSettings();
            Runtime = runtime ?? new PrintRuntimeSettings();
        }

        /// <summary>Приводит значения, приводимые вручную, к рабочим диапазонам.</summary>
        public void Normalize()
        {
            if (InitialCodeCount <= 0) InitialCodeCount = 10000;
            if (Printers == null) Printers = new List<PrinterSettings>();

            CodeFormat.Normalize();
            Runtime.Normalize();

            foreach (var printer in Printers)
            {
                if (string.IsNullOrWhiteSpace(printer.Id)) printer.Id = Guid.NewGuid().ToString("N");
                if (string.IsNullOrWhiteSpace(printer.FormatName)) printer.FormatName = "demo";
                if (string.IsNullOrWhiteSpace(printer.VariableName)) printer.VariableName = "code";
                if (printer.Port < 1 || printer.Port > 65535) printer.Port = 4100;
            }
        }

        /// <summary>Ключ Redis-буфера для принтера.</summary>
        public string BufferKey(string printerId)
        {
            return "labelprinter:buffer:" + printerId;
        }
    }
}
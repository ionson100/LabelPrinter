using System;

namespace LabelPrinter.Core.Configuration
{
    /// <summary>Параметры работы печатного движка: опрос, таймауты, триггер, буфер.</summary>
    public sealed class PrintRuntimeSettings
    {
        /// <summary>
        /// Как часто опрашивать счётчик напечатанных этикеток, мс.
        /// Это же пауза перед отправкой следующего кода, поэтому значение
        /// ограничивает темп печати: 100 мс ≈ 10 этикеток/с на принтер.
        /// </summary>
        public int PollIntervalMs { get; set; }

        /// <summary>Пауза перед повторной попыткой подключения, секунд.</summary>
        public int ReconnectDelaySeconds { get; set; }

        /// <summary>Таймаут обмена с принтером, мс.</summary>
        public int PrinterTimeoutMs { get; set; }

        /// <summary>Триггер печати: photocell | distance | timer.</summary>
        public string TriggerMode { get; set; }

        /// <summary>Вместимость буфера кодов.</summary>
        public int BufferSize { get; set; }

        /// <summary>Когда остаток в буфере опускается до этого значения — долить.</summary>
        public int BufferRefillThreshold { get; set; }

        /// <summary>
        /// Использовать принтер-симулятор вместо реального принтера.
        /// Нужен для проверки конвейера без оборудования.
        /// </summary>
        public bool TestPrinterMode { get; set; }

        /// <summary>Интервал печати в тестовом режиме, секунд.</summary>
        public int TestPrintIntervalSeconds { get; set; }

        public PrintRuntimeSettings()
        {
            PollIntervalMs = 100;
            ReconnectDelaySeconds = 5;
            PrinterTimeoutMs = 5000;
            TriggerMode = "photocell";
            BufferSize = 100;
            BufferRefillThreshold = 10;
            TestPrinterMode = false;
            TestPrintIntervalSeconds = 5;
        }

        public PrintRuntimeSettings Clone()
        {
            return (PrintRuntimeSettings)MemberwiseClone();
        }

        public void Normalize()
        {
            if (PollIntervalMs < 100) PollIntervalMs = 100;
            if (ReconnectDelaySeconds < 1) ReconnectDelaySeconds = 5;
            if (PrinterTimeoutMs < 200) PrinterTimeoutMs = 200;
            if (string.IsNullOrWhiteSpace(TriggerMode)) TriggerMode = "photocell";
            if (BufferSize < 1) BufferSize = 100;
            if (BufferRefillThreshold < 0) BufferRefillThreshold = 0;
            if (BufferRefillThreshold > BufferSize) BufferRefillThreshold = BufferSize / 2;
            if (TestPrintIntervalSeconds < 1) TestPrintIntervalSeconds = 5;
        }
    }
}
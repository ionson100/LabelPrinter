using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace LabelPrinter.Core.Protocol
{
    public enum PrinterLinkState
    {
        /// <summary>Соединения нет.</summary>
        Disconnected,

        /// <summary>Идёт подключение или переподключение.</summary>
        Connecting,

        /// <summary>Связь есть.</summary>
        Connected,

        /// <summary>Связь была и потеряна, идёт восстановление.</summary>
        Reconnecting
    }

    /// <summary>
    /// Единый интерфейс к принтеру. Реализации: настоящий принтер по протоколу
    /// APLINK и тестовый принтер-симулятор (режим «Тестовый принтер»).
    /// </summary>
    public interface IPrinterClient : IDisposable
    {
        string Name { get; }

        bool IsConnected { get; }

        PrinterLinkState State { get; }

        Task<bool> ConnectAsync(CancellationToken ct);

        void Disconnect();

        /// <summary>Сколько этикеток напечатано в текущем задании (GET_COUNTER → CUSTOM_COUNTER).</summary>
        Task<long> GetPrintedCountAsync(CancellationToken ct);

        /// <summary>Готов ли принтер печатать (GET_PRINTING_STATUS).</summary>
        Task<bool> GetPrintReadyAsync(CancellationToken ct);

        /// <summary>
        /// Загружает формат с очередным кодом и переводит принтер в состояние готов.
        /// Это SET_PRINTING_FORMAT + SET_PRINTING_STATUS.
        /// </summary>
        Task SendLabelAsync(string formatName, string variableName, string value, CancellationToken ct);

        /// <summary>true — печать (PRINT), false — пауза (PAUSE).</summary>
        Task SetPrintStatusAsync(bool printing, CancellationToken ct);

        /// <summary>Переключает принтер в печать или паузу (PRINT_CONFIG).</summary>
        Task SetTriggerAsync(string trigger, CancellationToken ct);

        Task<int> GetQueueItemCountAsync(CancellationToken ct);

        Task ClearQueueAsync(CancellationToken ct);

        Task<IReadOnlyList<string>> GetFormatListAsync(CancellationToken ct);

        Task<IReadOnlyList<string>> GetActiveErrorsAsync(CancellationToken ct);

        /// <summary>Краткое описание принтера для журнала и настроек.</summary>
        Task<string> DescribeAsync(CancellationToken ct);
    }
}

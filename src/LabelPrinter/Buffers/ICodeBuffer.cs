using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace LabelPrinter.Buffers
{
    /// <summary>
    /// Буфер кодов. По заданию живёт в Redis; InMemoryCodeBuffer — запасной вариант,
    /// чтобы программа оставалась работоспособной, если Redis поднять не удалось.
    ///
    /// Порядок выдачи — FIFO: коды кладутся в начало списка (LPUSH) и забираются
    /// с конца (RPOP).
    /// </summary>
    public interface ICodeBuffer : IDisposable
    {
        /// <summary>Вместимость буфера (из настроек, по умолчанию 100).</summary>
        int Capacity { get; set; }

        /// <summary>Человекочитаемое имя источника для журнала.</summary>
        string SourceName { get; }

        /// <summary>true, если буфер переживает перезапуск программы.</summary>
        bool IsPersistent { get; }

        Task<int> CountAsync(CancellationToken ct);

        /// <summary>Забирает один код. null — буфер пуст.</summary>
        Task<string> PopAsync(CancellationToken ct);

        /// <summary>Добавляет коды в начало очереди. Возвращает сколько добавилось.</summary>
        Task<int> PushAsync(IEnumerable<string> codes, CancellationToken ct);

        Task<int> ClearAsync(CancellationToken ct);
    }
}

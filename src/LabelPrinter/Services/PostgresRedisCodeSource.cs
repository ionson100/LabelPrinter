using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using LabelPrinter.Buffers;
using LabelPrinter.Configuration;
using LabelPrinter.Core.Abstractions;
using LabelPrinter.Core.Diagnostics;
using LabelPrinter.Data;

namespace LabelPrinter.Services
{
    /// <summary>
    /// Источник кодов на PostgreSQL + Redis — единственное место, где библиотека
    /// LabelPrinter.Core встречается с хранилищем.
    ///
    /// Политика ровно та, что задана для приложения:
    ///  • буфер живёт в Redis (список), FIFO: коды кладутся в начало, берутся с конца;
    ///  • остаток опустился до порога — доливаем до вместимости, то есть добираем
    ///    недостающее количество кодов из базы;
    ///  • взятый из базы код сразу помечается IsPrinted = true.
    /// </summary>
    public sealed class PostgresRedisCodeSource : ICodeSource, IDisposable
    {
        private readonly CodesRepository _repository;
        private readonly ICodeBuffer _buffer;
        private readonly AppSettings _settings;
        private readonly SemaphoreSlim _refillGate = new SemaphoreSlim(1, 1);

        public PostgresRedisCodeSource(CodesRepository repository, ICodeBuffer buffer, AppSettings settings)
        {
            _repository = repository ?? throw new ArgumentNullException("repository");
            _buffer = buffer ?? throw new ArgumentNullException("buffer");
            _settings = settings ?? throw new ArgumentNullException("settings");
        }

        /// <summary>Человекочитаемое имя источника для журнала.</summary>
        public string SourceName { get { return _buffer.SourceName; } }

        public Task<int> GetBufferCountAsync(CancellationToken cancellationToken)
        {
            return _buffer.CountAsync(cancellationToken);
        }

        public async Task<int> EnsureBufferAsync(int capacity, int threshold, CancellationToken cancellationToken)
        {
            await _refillGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                int count = await _buffer.CountAsync(cancellationToken).ConfigureAwait(false);
                if (count > threshold) return 0;

                int needed = capacity - count;
                if (needed <= 0) return 0;

                var codes = await _repository.ClaimAsync(needed, cancellationToken).ConfigureAwait(false);
                if (codes.Count == 0)
                {
                    Log.Warn("Свободных кодов в базе нет — буфер остался на " + count + " шт. " +
                             "Нажмите «Очистить буфер» для перезаливки базы.");
                    return 0;
                }

                await _buffer.PushAsync(codes, cancellationToken).ConfigureAwait(false);

                Log.Info("Буфер " + _buffer.SourceName + ": " + count + " → " + (count + codes.Count) +
                         " (взято из базы " + codes.Count + ", свободно осталось " +
                         await _repository.GetFreeAsync(cancellationToken).ConfigureAwait(false) + ").");
                return codes.Count;
            }
            finally
            {
                _refillGate.Release();
            }
        }

        public async Task<string> TakeAsync(CancellationToken cancellationToken)
        {
            int count = await _buffer.CountAsync(cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                await EnsureBufferAsync(_settings.Runtime.BufferSize, _settings.Runtime.BufferRefillThreshold, cancellationToken)
                    .ConfigureAwait(false);
            }

            // null — кодов больше нет. Это штатная ситуация, движок её обработает.
            return await _buffer.PopAsync(cancellationToken).ConfigureAwait(false);
        }

        public Task<long> GetRemainingAsync(CancellationToken cancellationToken)
        {
            return _repository.GetFreeAsync(cancellationToken);
        }

        public Task ClearAsync(CancellationToken cancellationToken)
        {
            return _buffer.ClearAsync(cancellationToken);
        }

        public void Dispose()
        {
            _refillGate.Dispose();
            _buffer.Dispose();
        }
    }
}
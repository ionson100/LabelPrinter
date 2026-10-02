using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using LabelPrinter.Buffers;
using LabelPrinter.Core;
using LabelPrinter.Data;

namespace LabelPrinter.Services
{
    /// <summary>В базе не осталось непечатанных кодов.</summary>
    public sealed class NoCodesAvailableException : Exception
    {
        public NoCodesAvailableException()
            : base("В базе закончились свободные коды. Нажмите «Очистить буфер», чтобы перезалить базу.")
        {
        }
    }

    /// <summary>
    /// Политика выдачи кодов: Redis-буфер + PostgreSQL как источник.
    ///
    /// По заданию:
    ///  • буфер живёт в Redis, вместимость по умолчанию 100;
    ///  • если буфер пуст — берём 100 кодов из базы и помечаем их напечатанными;
    ///  • если остаток опустился до 10 — доливаем до 100 (то есть добираем 100 − 10).
    /// </summary>
    public sealed class CodeSupply : IDisposable
    {
        private readonly CodesRepository _repository;
        private readonly ICodeBuffer _buffer;
        private readonly AppSettings _settings;
        private readonly SemaphoreSlim _refillGate = new SemaphoreSlim(1, 1);

        public CodeSupply(CodesRepository repository, ICodeBuffer buffer, AppSettings settings)
        {
            _repository = repository ?? throw new ArgumentNullException("repository");
            _buffer = buffer ?? throw new ArgumentNullException("buffer");
            _settings = settings ?? throw new ArgumentNullException("settings");
        }

        public ICodeBuffer Buffer { get { return _buffer; } }

        public int Capacity
        {
            get { return _settings.BufferSize; }
            set
            {
                _settings.BufferSize = value;
                _buffer.Capacity = value;
            }
        }

        public async Task<int> BufferCountAsync(CancellationToken ct)
        {
            return await _buffer.CountAsync(ct).ConfigureAwait(false);
        }

        public async Task<long> GetFreeAsync(CancellationToken ct)
        {
            return await _repository.GetFreeAsync(ct).ConfigureAwait(false);
        }

        public async Task<long> GetIssuedAsync(CancellationToken ct)
        {
            return await _repository.GetIssuedAsync(ct).ConfigureAwait(false);
        }

        /// <summary>
        /// Доливает буфер до вместимости, если остаток опустился до порога.
        /// Возвращает, сколько кодов добавлено.
        /// </summary>
        public async Task<int> RefillIfNeededAsync(CancellationToken ct)
        {
            await _refillGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                int count = await _buffer.CountAsync(ct).ConfigureAwait(false);
                int threshold = _settings.BufferRefillThreshold;

                if (count > threshold) return 0;

                int needed = _settings.BufferSize - count;
                if (needed <= 0) return 0;

                var codes = await _repository.ClaimAsync(needed, ct).ConfigureAwait(false);
                if (codes.Count == 0)
                {
                    Log.Warn("Свободных кодов в базе нет — буфер остался на " + count + " шт. " +
                             "Нажмите «Очистить буфер» для перезаливки базы.");
                    return 0;
                }

                await _buffer.PushAsync(codes, ct).ConfigureAwait(false);

                Log.Info("Буфер " + _buffer.SourceName + ": " + count + " → " + (count + codes.Count) +
                         " (взято из базы " + codes.Count + ", свободно осталось " +
                         await _repository.GetFreeAsync(ct).ConfigureAwait(false) + ").");
                return codes.Count;
            }
            finally
            {
                _refillGate.Release();
            }
        }

        /// <summary>
        /// Забирает один код на печать. При пустом буфере сначала доливает его.
        /// </summary>
        public async Task<string> TakeAsync(CancellationToken ct)
        {
            int count = await _buffer.CountAsync(ct).ConfigureAwait(false);
            if (count == 0)
            {
                await RefillIfNeededAsync(ct).ConfigureAwait(false);
            }

            var code = await _buffer.PopAsync(ct).ConfigureAwait(false);
            if (code == null)
            {
                throw new NoCodesAvailableException();
            }
            return code;
        }

        /// <summary>Принудительно добирает буфер до полной вместимости.</summary>
        public async Task<int> TopUpAsync(CancellationToken ct)
        {
            await _refillGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                int count = await _buffer.CountAsync(ct).ConfigureAwait(false);
                int needed = _settings.BufferSize - count;
                if (needed <= 0) return 0;

                var codes = await _repository.ClaimAsync(needed, ct).ConfigureAwait(false);
                await _buffer.PushAsync(codes, ct).ConfigureAwait(false);
                return codes.Count;
            }
            finally
            {
                _refillGate.Release();
            }
        }

        public Task<int> ClearBufferAsync(CancellationToken ct)
        {
            return _buffer.ClearAsync(ct);
        }

        public void Dispose()
        {
            _refillGate.Dispose();
            _buffer.Dispose();
        }
    }
}

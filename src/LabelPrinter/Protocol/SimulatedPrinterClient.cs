using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using LabelPrinter.Core;

namespace LabelPrinter.Protocol
{
    /// <summary>
    /// Принтер-симулятор для режима «Тестовый принтер».
    ///
    /// Работает без сети: считает напечатанные этикетки и увеличивает счётчик
    /// на 1 через заданный интервал (по заданию — 5 секунд), как только в него
    /// загружен код. Так весь конвейер (Redis → БД → принтер) проверяется целиком,
    /// не трогая настоящее оборудование.
    /// </summary>
    public sealed class SimulatedPrinterClient : IPrinterClient
    {
        private readonly string _name;
        private readonly int _intervalSeconds;
        private readonly object _gate = new object();

        private PrinterLinkState _state = PrinterLinkState.Disconnected;
        private Timer _timer;

        /// <summary>Сколько этикеток «напечатал» симулятор — аналог CUSTOM_COUNTER.</summary>
        private long _printed;

        /// <summary>Код, ожидающий печати (аналог переменной в формате).</summary>
        private string _armedCode;

        /// <summary>Кто напечатал последнюю этикетку — для журнала.</summary>
        private string _lastCode;

        private bool _printEnabled;

        public SimulatedPrinterClient(string name, int intervalSeconds)
        {
            _name = string.IsNullOrWhiteSpace(name) ? "Тестовый принтер" : name;
            _intervalSeconds = intervalSeconds < 1 ? 5 : intervalSeconds;
        }

        public string Name { get { return _name; } }

        public bool IsConnected { get { return _state == PrinterLinkState.Connected; } }

        public PrinterLinkState State { get { return _state; } }

        public Task<bool> ConnectAsync(CancellationToken ct)
        {
            _state = PrinterLinkState.Connected;
            Log.Success("Тестовый принтер «" + _name + "» подключён (интервал " + _intervalSeconds + " с).");
            return Task.FromResult(true);
        }

        public void Disconnect()
        {
            StopTimer();
            lock (_gate)
            {
                _armedCode = null;
                _printEnabled = false;
            }
            _state = PrinterLinkState.Disconnected;
        }

        public void MarkLost()
        {
            _state = PrinterLinkState.Reconnecting;
        }

        public Task<long> GetPrintedCountAsync(CancellationToken ct)
        {
            lock (_gate) return Task.FromResult(_printed);
        }

        public Task<bool> GetPrintReadyAsync(CancellationToken ct)
        {
            lock (_gate) return Task.FromResult(_printEnabled);
        }

        public Task SendLabelAsync(string formatName, string variableName, string value, CancellationToken ct)
        {
            lock (_gate)
            {
                _armedCode = value;
                _lastCode = value;
            }
            StartTimer();
            Log.Info("[" + _name + "] код передан в принтер: " + Codes.CodeFactory.ToHumanReadable(value));
            return Task.CompletedTask;
        }

        public Task SetPrintStatusAsync(bool printing, CancellationToken ct)
        {
            lock (_gate)
            {
                _printEnabled = printing;
            }

            if (!printing)
            {
                StopTimer();
            }
            else
            {
                StartTimer();
            }
            return Task.CompletedTask;
        }

        public Task SetTriggerAsync(string trigger, CancellationToken ct)
        {
            return Task.CompletedTask;
        }

        public Task<int> GetQueueItemCountAsync(CancellationToken ct)
        {
            return Task.FromResult(0);
        }

        public Task ClearQueueAsync(CancellationToken ct)
        {
            StopTimer();
            lock (_gate) _armedCode = null;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<string>> GetFormatListAsync(CancellationToken ct)
        {
            return Task.FromResult<IReadOnlyList<string>>(new[] { "demo" });
        }

        public Task<IReadOnlyList<string>> GetActiveErrorsAsync(CancellationToken ct)
        {
            return Task.FromResult<IReadOnlyList<string>>(new string[0]);
        }

        public Task<string> DescribeAsync(CancellationToken ct)
        {
            return Task.FromResult("симулятор, интервал " + _intervalSeconds + " с");
        }

        // ------------------------------------------------------------------

        private void StartTimer()
        {
            StopTimer();
            bool ready;
            lock (_gate) ready = _printEnabled && _armedCode != null;
            if (!ready) return;

            _timer = new Timer(_ => Tick(), null,
                TimeSpan.FromSeconds(_intervalSeconds),
                TimeSpan.FromSeconds(_intervalSeconds));
        }

        private void StopTimer()
        {
            var timer = Interlocked.Exchange(ref _timer, null);
            if (timer != null)
            {
                timer.Dispose();
            }
        }

        private void Tick()
        {
            string code = null;

            lock (_gate)
            {
                if (!_printEnabled || _armedCode == null) return;

                _printed++;
                code = _armedCode;
                _armedCode = null;   // одна этикетка — один расход кода
            }

            Log.Info("[" + _name + "] напечатана этикетка №" + _printed + ": " +
                     Codes.CodeFactory.ToHumanReadable(code));

            // Следующий код приложение подгрузит само, увидев рост счётчика.
        }

        public void Dispose()
        {
            Disconnect();
        }
    }
}

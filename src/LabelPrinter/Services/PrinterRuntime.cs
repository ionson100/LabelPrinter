using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using LabelPrinter.Codes;
using LabelPrinter.Core;
using LabelPrinter.Protocol;

namespace LabelPrinter.Services
{
    /// <summary>
    /// Один принтер: подключение с автопереподключением, конвейер кодов и счётчики.
    ///
    /// Рабочий цикл (по заданию):
    ///  1. при старте печати спрашиваем у принтера количество напечатанных этикеток;
    ///  2. берём код из буфера (Redis), а если буфер пуст или близок к концу —
    ///     добираем его кодами из базы и помечаем их напечатанными;
    ///  3. отдаём код принтеру и переводим его в состояние готов;
    ///  4. принтер печатает по внешнему триггеру; когда счётчик напечатанных
    ///     вырос — берём следующий код.
    /// </summary>
    public sealed class PrinterRuntime : ObservableObject, IDisposable
    {
        private readonly CodeSupply _supply;
        private readonly AppSettings _settings;
        private readonly IPrinterClient _client;

        private CancellationTokenSource _cts;
        private Task _loop;
        private readonly object _gate = new object();

        /// <summary>Базовое значение счётчика, от которого считаем прирост.</summary>
        private long _baselinePrinted;
        private bool _baselineTaken;

        private bool _printing;
        private bool _disposed;

        private bool _isConnected;
        private string _linkText;
        private long _printedThisSession;
        private long _issuedThisSession;
        private string _armedCode;
        private int _bufferCount;
        private long _freeCodes;
        private string _lastError;

        public PrinterSettings Printer { get; private set; }

        public CodeSupply Supply { get { return _supply; } }

        public IPrinterClient Client { get { return _client; } }

        // ---------------- наблюдаемое состояние для интерфейса ----------------

        /// <summary>Лампочка: true — связь есть (зелёный), false — нет (красный).</summary>
        public bool IsConnected
        {
            get { return _isConnected; }
            private set { Set(ref _isConnected, value); }
        }

        public string LinkText
        {
            get { return _linkText; }
            private set { Set(ref _linkText, value); }
        }

        /// <summary>Сколько этикеток напечатано за текущий сеанс печати.</summary>
        public long PrintedThisSession
        {
            get { return _printedThisSession; }
            private set { Set(ref _printedThisSession, value); }
        }

        /// <summary>Сколько кодов отдано принтеру за текущий сеанс.</summary>
        public long IssuedThisSession
        {
            get { return _issuedThisSession; }
            private set { Set(ref _issuedThisSession, value); }
        }

        /// <summary>Код, который сейчас заряжен в принтер и ждёт внешнего триггера.</summary>
        public string ArmedCode
        {
            get { return _armedCode; }
            private set { Set(ref _armedCode, value); }
        }

        public int BufferCount
        {
            get { return _bufferCount; }
            set { Set(ref _bufferCount, value); }
        }

        public long FreeCodes
        {
            get { return _freeCodes; }
            set { Set(ref _freeCodes, value); }
        }

        public string LastError
        {
            get { return _lastError; }
            private set { Set(ref _lastError, value); }
        }

        public bool IsPrinting
        {
            get { return _printing; }
            private set { Set(ref _printing, value); }
        }

        public event EventHandler StateChanged;

        public PrinterRuntime(PrinterSettings printer, IPrinterClient client, CodeSupply supply, AppSettings settings)
        {
            Printer = printer ?? throw new ArgumentNullException("printer");
            _client = client ?? throw new ArgumentNullException("client");
            _supply = supply ?? throw new ArgumentNullException("supply");
            _settings = settings ?? throw new ArgumentNullException("settings");

            LinkText = "не подключён";
        }

        // ------------------------------------------------------------------
        //  Жизненный цикл соединения
        // ------------------------------------------------------------------

        public void Start()
        {
            lock (_gate)
            {
                if (_loop != null) return;

                _cts = new CancellationTokenSource();
                var token = _cts.Token;
                _loop = Task.Run(() => RunLoopAsync(token), token);
            }
        }

        public async Task StopAsync()
        {
            Task loop;
            CancellationTokenSource cts;

            lock (_gate)
            {
                loop = _loop;
                cts = _cts;
                _loop = null;
                _cts = null;
            }

            if (cts != null) cts.Cancel();

            if (_printing)
            {
                await TryPauseAsync(CancellationToken.None).ConfigureAwait(false);
            }

            if (loop != null)
            {
                try
                {
                    await Task.WhenAny(loop, Task.Delay(2000)).ConfigureAwait(false);
                }
                catch
                {
                    // завершение не критично
                }
            }

            try { _client.Disconnect(); } catch { /* уже отключён */ }
            if (cts != null) cts.Dispose();

            SetConnected(false, "остановлен");
        }

        // ------------------------------------------------------------------
        //  Печать
        // ------------------------------------------------------------------

        /// <summary>
        /// Старт печати: узнаём у принтера текущее количество напечатанных этикеток
        /// и заряжаем первый код.
        /// </summary>
        public async Task StartPrintingAsync(CancellationToken ct)
        {
            if (!IsConnected)
            {
                throw new InvalidOperationException("Принтер «" + Printer.Name + "» не подключён.");
            }

            // Шаг 1 задания: спрашиваем количество напечатанных этикеток.
            _baselinePrinted = await _client.GetPrintedCountAsync(ct).ConfigureAwait(false);
            _baselineTaken = true;

            Log.Info("[" + Printer.Name + "] начало печати. Принтер сообщает " + _baselinePrinted +
                     " напечатанных этикеток (счётчик задания).");

            // Триггер печати: по умолчанию внешний (фотодатчик).
            try
            {
                await _client.SetTriggerAsync(_settings.TriggerMode, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Warn("[" + Printer.Name + "] не удалось выставить триггер «" + _settings.TriggerMode +
                         "»: " + ex.Message);
            }

            // Наполняем буфер до полной вместимости перед стартом.
            await _supply.TopUpAsync(ct).ConfigureAwait(false);

            PrintedThisSession = 0;
            IssuedThisSession = 0;

            _printing = true;
            await ArmNextAsync(ct).ConfigureAwait(false);

            Log.Success("[" + Printer.Name + "] печать запущена.");
        }

        /// <summary>Остановка печати: принтер уходит на паузу.</summary>
        public async Task StopPrintingAsync(CancellationToken ct)
        {
            if (!_printing) return;
            _printing = false;

            await TryPauseAsync(ct).ConfigureAwait(false);

            if (!string.IsNullOrEmpty(ArmedCode))
            {
                Log.Warn("[" + Printer.Name + "] код " + CodeFactory.ToHumanReadable(ArmedCode) +
                         " был заряжен, но не напечатан — он считается израсходованным.");
            }

            ArmedCode = null;
            Raise();
            Log.Info("[" + Printer.Name + "] печать остановлена. Итого напечатано: " + PrintedThisSession + ".");
        }

        private async Task RunLoopAsync(CancellationToken ct)
        {
            int pollMs = Math.Max(100, _settings.PollIntervalMs);
            int reconnectMs = Math.Max(1, _settings.ReconnectDelaySeconds) * 1000;

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    if (!await EnsureConnectedAsync(ct).ConfigureAwait(false))
                    {
                        await DelayAsync(reconnectMs, ct).ConfigureAwait(false);
                        continue;
                    }

                    if (_printing)
                    {
                        await PollCounterAsync(ct).ConfigureAwait(false);
                    }

                    await DelayAsync(pollMs, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    LastError = ex.Message;
                    Log.Error("[" + Printer.Name + "] сбой в рабочем цикле: " + ex.Message);
                    HandleLinkLoss();
                    Raise();

                    try
                    {
                        await DelayAsync(reconnectMs, ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }
        }

        private async Task<bool> EnsureConnectedAsync(CancellationToken ct)
        {
            if (_client.IsConnected) return true;

            if (IsConnected) SetConnected(false, "переподключение…");

            try
            {
                Log.Info("[" + Printer.Name + "] подключение к " + Printer.Endpoint + "…");
                await _client.ConnectAsync(ct).ConfigureAwait(false);
                LastError = null;
                SetConnected(true, "подключён");

                try
                {
                    var description = await _client.DescribeAsync(ct).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(description))
                    {
                        Log.Info("[" + Printer.Name + "] " + description + ".");
                    }
                }
                catch
                {
                    // описание не критично
                }
                return true;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                SetConnected(false, "нет связи");
                Log.Warn("[" + Printer.Name + "] не удалось подключиться к " + Printer.Endpoint +
                         ": " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Следит за счётчиком напечатанных этикеток. Рост счётчика означает, что
        /// внешний триггер сработал и этикетка напечатана — пора брать следующий код.
        ///
        /// Отсчёт ведётся от значения, снятого при старте печати (см. StartPrintingAsync),
        /// а не от нуля: иначе счётчик, накопленный до старта программы, был бы учтён
        /// как напечатанный в этом сеансе.
        ///
        /// Счётчик у принтера только растёт, поэтому ветки на уменьшение нет —
        /// по условию эксплуатации такое состояние невозможно.
        /// </summary>
        private async Task PollCounterAsync(CancellationToken ct)
        {
            long current = await _client.GetPrintedCountAsync(ct).ConfigureAwait(false);

            if (!_baselineTaken)
            {
                _baselinePrinted = current;
                _baselineTaken = true;
                return;
            }

            if (current > _baselinePrinted)
            {
                long delta = current - _baselinePrinted;
                _baselinePrinted = current;
                PrintedThisSession += delta;

                if (delta > 1)
                {
                    Log.Warn("[" + Printer.Name + "] счётчик вырос сразу на " + delta +
                             " — напечатано больше одной этикетки за такт.");
                }

                Log.Info("[" + Printer.Name + "] напечатано: " + PrintedThisSession +
                         " (счётчик принтера " + current + ").");

                ArmedCode = null;
                Raise();

                if (_printing)
                {
                    await ArmNextAsync(ct).ConfigureAwait(false);
                }
            }
        }

        /// <summary>Берёт код и передаёт принтеру, переводя его в состояние готов.</summary>
        private async Task ArmNextAsync(CancellationToken ct)
        {
            string code;
            try
            {
                code = await _supply.TakeAsync(ct).ConfigureAwait(false);
            }
            catch (NoCodesAvailableException ex)
            {
                Log.Error("[" + Printer.Name + "] " + ex.Message);
                await TryPauseAsync(ct).ConfigureAwait(false);
                _printing = false;
                Raise();
                return;
            }
            catch (Exception ex)
            {
                Log.Error("[" + Printer.Name + "] не удалось взять код: " + ex.Message);
                await TryPauseAsync(ct).ConfigureAwait(false);
                return;
            }

            try
            {
                // Шаг 3 задания: код уходит в принтер, принтер переводится в «готов».
                await _client.SendLabelAsync(Printer.FormatName, Printer.VariableName, code, ct).ConfigureAwait(false);
                await _client.SetPrintStatusAsync(true, ct).ConfigureAwait(false);

                ArmedCode = code;
                IssuedThisSession++;

                // Держим буфер «на подходе» — доливаем заранее, а не в момент печати.
                await _supply.RefillIfNeededAsync(ct).ConfigureAwait(false);

                Raise();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log.Error("[" + Printer.Name + "] принтер не принял код " +
                          CodeFactory.ToHumanReadable(code) + ": " + ex.Message);
                HandleLinkLoss();
                Raise();
            }
        }

        private async Task TryPauseAsync(CancellationToken ct)
        {
            try
            {
                await _client.SetPrintStatusAsync(false, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Warn("[" + Printer.Name + "] не удалось поставить принтер на паузу: " + ex.Message);
            }
        }

        private void HandleLinkLoss()
        {
            if (_client is RealPrinterClient real)
            {
                real.MarkLost();
            }
            else
            {
                _client.Disconnect();
            }
            SetConnected(false, "потеряна связь, переподключение…");
        }

        private void SetConnected(bool connected, string text)
        {
            IsConnected = connected;
            LinkText = text;
            Raise();
        }

        private void Raise()
        {
            EventHandler handler = StateChanged;
            if (handler != null) handler(this, EventArgs.Empty);
        }

        /// <summary>
        /// Попросить интерфейс перечитать состояние. Событие нельзя вызвать извне,
        /// поэтому нужен этот публичный метод.
        /// </summary>
        public void NotifyStateChanged()
        {
            Raise();
        }

        private static async Task DelayAsync(int milliseconds, CancellationToken ct)
        {
            try
            {
                await Task.Delay(milliseconds, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // штатная остановка
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try { _client.Dispose(); } catch { /* уже закрыт */ }
            _supply.Dispose();

            var cts = Interlocked.Exchange(ref _cts, null);
            if (cts != null) cts.Dispose();
        }
    }
}

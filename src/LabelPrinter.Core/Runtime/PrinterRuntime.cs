using System;
using System.Threading;
using System.Threading.Tasks;
using LabelPrinter.Core.Abstractions;
using LabelPrinter.Core.Codes;
using LabelPrinter.Core.Configuration;
using LabelPrinter.Core.Diagnostics;
using LabelPrinter.Core.Protocol;

namespace LabelPrinter.Core.Runtime
{
    /// <summary>
    /// Один принтер: подключение с автопереподключением, конвейер кодов и счётчики.
    ///
    /// Рабочий цикл:
    ///  1. при старте печати спрашиваем у принтера количество напечатанных этикеток
    ///     и запоминаем его как базу — не ноль, иначе счётчик, накопленный до
    ///     запуска программы, попал бы в статистику сеанса;
    ///  2. берём код из источника (буфер, а если он пуст или близок к концу —
    ///     источник сам доливает его из своего хранилища);
    ///  3. отдаём код принтеру и переводим его в состояние готов;
    ///  4. принтер печатает по внешнему триггеру; когда счётчик напечатанных
    ///     вырос — берём следующий код.
    ///
    /// Счётчик принтера по условию эксплуатации только растёт, поэтому ветки
    /// на уменьшение нет.
    /// </summary>
    public sealed class PrinterRuntime : ObservableObjectBase, IDisposable
    {
        private readonly ICodeSource _source;
        private readonly PrintRuntimeSettings _settings;
        private readonly CodeFormatSettings _codeFormat;
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

        /// <summary>Сколько кодов осталось в хранилище, по данным источника.</summary>
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

        /// <summary>Сработало при изменении состояния — для перерисовки интерфейса.</summary>
        public event EventHandler StateChanged;

        public PrinterRuntime(PrinterSettings printer,
                              IPrinterClient client,
                              ICodeSource source,
                              PrintRuntimeSettings settings,
                              CodeFormatSettings codeFormat)
        {
            Printer = printer ?? throw new ArgumentNullException("printer");
            _client = client ?? throw new ArgumentNullException("client");
            _source = source ?? throw new ArgumentNullException("source");
            _settings = settings ?? throw new ArgumentNullException("settings");
            _codeFormat = codeFormat ?? new CodeFormatSettings();

            _settings.Normalize();
            _codeFormat.Normalize();

            _linkText = "не подключён";
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
                try { await Task.WhenAny(loop, Task.Delay(2000)).ConfigureAwait(false); }
                catch { }
            }

            try { _client.Disconnect(); } catch { }
            if (cts != null) cts.Dispose();

            SetConnected(false, "остановлен");
        }

        // ------------------------------------------------------------------
        //  Печать
        // ------------------------------------------------------------------

        /// <summary>
        /// Старт печати: узнаём у принтера текущее количество напечатанных
        /// этикеток и заряжаем первый код.
        /// </summary>
        public async Task StartPrintingAsync(CancellationToken cancellationToken)
        {
            if (!IsConnected)
            {
                throw new InvalidOperationException("Принтер «" + Printer.Name + "» не подключён.");
            }

            // Шаг 1: спрашиваем количество напечатанных этикеток и запоминаем базу.
            _baselinePrinted = await _client.GetPrintedCountAsync(cancellationToken).ConfigureAwait(false);
            _baselineTaken = true;

            Log.Info("[" + Printer.Name + "] начало печати. Принтер сообщает " + _baselinePrinted +
                     " напечатанных этикеток (счётчик задания).");

            try
            {
                await _client.SetTriggerAsync(_settings.TriggerMode, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Warn("[" + Printer.Name + "] не удалось выставить триггер «" + _settings.TriggerMode +
                         "»: " + ex.Message);
            }

            // Наполняем буфер до полной вместимости перед стартом.
            await RefillAsync(cancellationToken).ConfigureAwait(false);

            PrintedThisSession = 0;
            IssuedThisSession = 0;

            _printing = true;
            await ArmNextAsync(cancellationToken).ConfigureAwait(false);

            Log.Success("[" + Printer.Name + "] печать запущена.");
        }

        /// <summary>Остановка печати: принтер уходит на паузу.</summary>
        public async Task StopPrintingAsync(CancellationToken cancellationToken)
        {
            if (!_printing) return;
            _printing = false;

            await TryPauseAsync(cancellationToken).ConfigureAwait(false);

            if (!string.IsNullOrEmpty(ArmedCode))
            {
                Log.Warn("[" + Printer.Name + "] код " + CodeFactory.ToHumanReadable(ArmedCode) +
                         " был заряжен, но не напечатан — он считается израсходованным.");
            }

            ArmedCode = null;
            Raise();
            Log.Info("[" + Printer.Name + "] печать остановлена. Итого напечатано: " + PrintedThisSession + ".");
        }

        // ------------------------------------------------------------------
        //  Основной цикл
        // ------------------------------------------------------------------

        private async Task RunLoopAsync(CancellationToken cancellationToken)
        {
            int pollMs = _settings.PollIntervalMs;
            int reconnectMs = _settings.ReconnectDelaySeconds * 1000;

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    if (!await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false))
                    {
                        await DelayAsync(reconnectMs, cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    if (_printing)
                    {
                        await PollCounterAsync(cancellationToken).ConfigureAwait(false);
                    }

                    await DelayAsync(pollMs, cancellationToken).ConfigureAwait(false);
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

                    try { await DelayAsync(reconnectMs, cancellationToken).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                }
            }
        }

        private async Task<bool> EnsureConnectedAsync(CancellationToken cancellationToken)
        {
            if (_client.IsConnected) return true;

            if (IsConnected) SetConnected(false, "переподключение…");

            try
            {
                Log.Info("[" + Printer.Name + "] подключение к " + Printer.Endpoint + "…");
                await _client.ConnectAsync(cancellationToken).ConfigureAwait(false);
                LastError = null;
                SetConnected(true, "подключён");

                try
                {
                    var description = await _client.DescribeAsync(cancellationToken).ConfigureAwait(false);
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
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                SetConnected(false, "нет связи");
                Log.Warn("[" + Printer.Name + "] не удалось подключиться к " + Printer.Endpoint + ": " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Следит за счётчиком напечатанных этикеток. Рост счётчика означает, что
        /// внешний триггер сработал и этикетка напечатана — пора брать следующий код.
        /// </summary>
        private async Task PollCounterAsync(CancellationToken cancellationToken)
        {
            long current = await _client.GetPrintedCountAsync(cancellationToken).ConfigureAwait(false);

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
                    await ArmNextAsync(cancellationToken).ConfigureAwait(false);
                }
            }
        }

        /// <summary>Берёт код и передаёт принтеру, переводя его в состояние готов.</summary>
        private async Task ArmNextAsync(CancellationToken cancellationToken)
        {
            string code;
            try
            {
                code = await _source.TakeAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Error("[" + Printer.Name + "] не удалось взять код: " + ex.Message);
                await TryPauseAsync(cancellationToken).ConfigureAwait(false);
                return;
            }

            if (string.IsNullOrEmpty(code))
            {
                Log.Error("[" + Printer.Name + "] коды закончились. Печать остановлена. " +
                          "Наполните хранилище и нажмите «Печать» снова.");
                await TryPauseAsync(cancellationToken).ConfigureAwait(false);
                _printing = false;
                Raise();
                return;
            }

            try
            {
                await _client.SendLabelAsync(Printer.FormatName, Printer.VariableName, code, cancellationToken)
                             .ConfigureAwait(false);
                await _client.SetPrintStatusAsync(true, cancellationToken).ConfigureAwait(false);

                ArmedCode = code;
                IssuedThisSession++;

                // Держим буфер «на подходе» — доливаем заранее, а не в момент печати.
                await RefillAsync(cancellationToken).ConfigureAwait(false);

                Raise();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
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

        /// <summary>Просит источник долить буфер до полной вместимости.</summary>
        private async Task RefillAsync(CancellationToken cancellationToken)
        {
            try
            {
                await _source.EnsureBufferAsync(_settings.BufferSize, _settings.BufferRefillThreshold, cancellationToken)
                           .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log.Warn("[" + Printer.Name + "] не удалось долить буфер: " + ex.Message);
            }
        }

        private async Task TryPauseAsync(CancellationToken cancellationToken)
        {
            try
            {
                await _client.SetPrintStatusAsync(false, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Warn("[" + Printer.Name + "] не удалось поставить принтер на паузу: " + ex.Message);
            }
        }

        private void HandleLinkLoss()
        {
            var real = _client as RealPrinterClient;
            if (real != null) real.MarkLost();
            else _client.Disconnect();

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

        private static async Task DelayAsync(int milliseconds, CancellationToken cancellationToken)
        {
            try { await Task.Delay(milliseconds, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try { _client.Dispose(); } catch { }

            var cts = Interlocked.Exchange(ref _cts, null);
            if (cts != null) cts.Dispose();
        }
    }

    /// <summary>
    /// Минимальная база для наблюдаемых объектов внутри библиотеки.
    /// Отдельный класс, чтобы приложение не было обязано подставлять свой
    /// LabelPrinter.Infrastructure.ObservableObject.
    /// </summary>
    public abstract class ObservableObjectBase : System.ComponentModel.INotifyPropertyChanged
    {
        public event System.ComponentModel.PropertyChangedEventHandler PropertyChanged;

        protected void Raise([System.Runtime.CompilerServices.CallerMemberName] string propertyName = null)
        {
            var handler = PropertyChanged;
            if (handler != null)
            {
                handler(this, new System.ComponentModel.PropertyChangedEventArgs(propertyName));
            }
        }

        protected bool Set<T>(ref T field, T value,
                              [System.Runtime.CompilerServices.CallerMemberName] string propertyName = null)
        {
            if (System.Collections.Generic.EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            Raise(propertyName);
            return true;
        }
    }
}
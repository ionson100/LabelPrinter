using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LabelPrinter.Buffers;
using LabelPrinter.Configuration;
using LabelPrinter.Core.Codes;
using LabelPrinter.Core.Diagnostics;
using LabelPrinter.Core.Runtime;
using LabelPrinter.Data;

namespace LabelPrinter.Services
{
    /// <summary>
    /// Слой приложения над <see cref="PrintEngine"/>.
    ///
    /// Движок ничего не знает про базу и Redis — здесь создаётся источник кодов,
    /// наполняется таблица и реализуется кнопка «Очистить буфер».
    /// Модель представления работает только с этим классом.
    /// </summary>
    public sealed class PrintService : IDisposable
    {
        private readonly AppSettings _settings;
        private readonly CodesRepository _repository;

        /// <summary>Текущий источник кодов. При пересборке движка заменяется.</summary>
        private IDisposable _currentSource;

        private CancellationTokenSource _metricsCts;
        private Task _metricsLoop;

        private bool _disposed;

        public AppSettings Settings { get { return _settings; } }

        public CodesRepository Repository { get { return _repository; } }

        public PrintEngine Engine { get; private set; }

        public PrintMetrics Metrics { get { return Engine == null ? new PrintMetrics() : Engine.Metrics; } }

        /// <summary>
        /// Состав принтеров изменился.
        ///
        /// Событие своё, а не переадресация в движок: движок создаётся позже,
        /// и переадресация в момент подписки молча теряла бы подписчика.
        /// </summary>
        public event EventHandler StateChanged;

        private void OnEngineStateChanged(object sender, EventArgs e)
        {
            var handler = StateChanged;
            if (handler != null) handler(this, e);
        }

        public IReadOnlyList<PrinterRuntime> GetRuntimes()
        {
            return Engine == null ? new List<PrinterRuntime>() : Engine.Runtimes;
        }

        public bool IsPrinting { get { return Engine != null && Engine.IsPrinting; } }

        /// <summary>
        /// Сколько кодов помечено IsPrinted = true. Это показатель хранилища,
        /// движок о нём не знает, поэтому он живёт здесь.
        /// </summary>
        public long IssuedCodes { get; private set; }

        /// <summary>Сколько всего кодов в таблице.</summary>
        public long TotalCodes { get; private set; }

        public PrintService(AppSettings settings)
        {
            _settings = settings ?? throw new ArgumentNullException("settings");
            _settings.Normalize();

            _repository = new CodesRepository(_settings.ConnectionString);
        }

        // ------------------------------------------------------------------
        //  Запуск программы
        // ------------------------------------------------------------------

        /// <summary>
        /// Проверяет и при необходимости создаёт таблицу Codes, затем наполняет её
        /// кодами. Вызывается один раз при старте.
        /// </summary>
        public async Task<InitializationResult> InitializeDatabaseAsync(CancellationToken cancellationToken)
        {
            Log.Info("PostgreSQL: проверка таблицы «" + CodesRepository.TableName + "».");

            var result = await _repository.InitializeAsync(
                _settings.InitialCodeCount,
                () => CodeFactory.Build(_settings.CodeFormat),
                cancellationToken).ConfigureAwait(false);

            if (result.Skipped)
            {
                Log.Info("Таблица «" + CodesRepository.TableName + "» уже заполнена: всего " +
                         result.Total + ", свободно " + result.Free + ".");
            }
            else
            {
                Log.Success("База готова. " + result.Summary);
            }

            Log.Info("Генератор кодов: " + CodeFactory.Describe(_settings.CodeFormat));

            var gtinProblem = CodeFactory.ValidateGtin14(_settings.CodeFormat.Gtin14);
            if (gtinProblem != null)
            {
                // Не мешаем работе, но обязаны сказать: сканер может отвергнуть такие коды.
                Log.Warn("GTIN-14 «" + _settings.CodeFormat.Gtin14 + "»: " + gtinProblem +
                         " Коды будут сформированы как есть, но часть сканеров их не примет.");
            }

            WarnAboutTemplateChange();

            return result;
        }

        /// <summary>
        /// Смена шаблона кода не меняет уже залитые строки в базе — они останутся
        /// в старом формате и уйдут на этикетки. Сообщаем об этом явно.
        /// </summary>
        private void WarnAboutTemplateChange()
        {
            var current = _settings.CodeTemplate;

            if (string.IsNullOrEmpty(_settings.LastCodeTemplate))
            {
                // Первый запуск: формат, под который база будет заполнена.
                _settings.LastCodeTemplate = current;
                SettingsService.Save(_settings);
                return;
            }

            if (!string.Equals(_settings.LastCodeTemplate, current, StringComparison.Ordinal))
            {
                Log.Warn("ФОРМАТ КОДА ИЗМЕНЁН. База наполнена по шаблону «" +
                         _settings.LastCodeTemplate + "», а печать пойдёт по «" + current +
                         "». Уже записанные коды останутся в старом формате — " +
                         "нажмите «Очистить буфер», чтобы перезалить базу.");

                _settings.LastCodeTemplate = current;
                SettingsService.Save(_settings);
            }
        }

        // ------------------------------------------------------------------
        //  Принтеры
        // ------------------------------------------------------------------

        /// <summary>Создаёт источник кодов и запускает движок для всех принтеров.</summary>
        public async Task RebuildPrintersAsync(CancellationToken cancellationToken)
        {
            var engine = new PrintEngine(_settings.Runtime, _settings.CodeFormat);

            // Движок работает с одним источником на все принтеры: буфер общий,
            // коды при этом не пересекаются, потому что выдача из базы помечает
            // их IsPrinted = true в одной транзакции.
            var buffer = await CodeBufferFactory.CreateAsync(
                _settings.RedisConnectionString,
                _settings.BufferKey("shared"),
                _settings.Runtime.BufferSize,
                cancellationToken).ConfigureAwait(false);

            var source = new PostgresRedisCodeSource(_repository, buffer, _settings);
            engine.CodeSource = source;

            foreach (var printer in _settings.Printers)
            {
                engine.Printers.Add(printer);
            }

            var previous = Engine;
            Engine = engine;

            try
            {
                if (previous != null)
                {
                    previous.StateChanged -= OnEngineStateChanged;
                    previous.Dispose();
                }

                // Старый источник больше не нужен: его буфер закрыт, а новый
                // открыт выше. Иначе на каждом «Применить» копился бы мусор.
                var oldSource = Interlocked.Exchange(ref _currentSource, source);
                if (oldSource != null)
                {
                    try { oldSource.Dispose(); } catch { }
                }
            }
            catch
            {
                // старое соединение могло не завершиться — это не мешает новому
                _currentSource = source;
            }

            engine.StateChanged += OnEngineStateChanged;

            // StartAsync создаёт рабочие объекты принтеров и запускает циклы
            // подключения; внутри он же обновляет показатели.
            await engine.StartAsync(cancellationToken).ConfigureAwait(false);

            // Сообщаем ещё раз на случай, если интерфейс подписался позже.
            OnEngineStateChanged(this, EventArgs.Empty);

            StartMetricsLoop();
        }

        // ------------------------------------------------------------------
        //  Печать
        // ------------------------------------------------------------------

        public async Task StartPrintingAsync(CancellationToken cancellationToken)
        {
            if (Engine == null) throw new InvalidOperationException("Принтеры не инициализированы.");
            await Engine.StartPrintingAsync(cancellationToken).ConfigureAwait(false);
        }

        public async Task StopPrintingAsync(CancellationToken cancellationToken)
        {
            if (Engine == null) return;
            await Engine.StopPrintingAsync(cancellationToken).ConfigureAwait(false);
        }

        public async Task RefreshMetricsAsync(CancellationToken cancellationToken)
        {
            if (Engine != null)
            {
                await Engine.RefreshMetricsAsync(cancellationToken).ConfigureAwait(false);
            }

            // Показатели хранилища — их движок не ведёт.
            try
            {
                IssuedCodes = await _repository.GetIssuedAsync(cancellationToken).ConfigureAwait(false);
                TotalCodes = await _repository.GetTotalAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // показатели не критичны
            }
        }

        /// <summary>
        /// Периодическое обновление показателей хранилища.
        /// Движок сам обновляет свои (буфер, напечатано, связь), а счётчики
        /// таблицы «Codes» живут в приложении — их тоже нужно подтягивать,
        /// иначе строка состояния показывала бы нули.
        /// </summary>
        private void StartMetricsLoop()
        {
            if (_metricsLoop != null) return;

            _metricsCts = new CancellationTokenSource();
            var token = _metricsCts.Token;

            _metricsLoop = Task.Run(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    try { await RefreshMetricsAsync(token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                    catch { /* показатели не критичны */ }

                    try { await Task.Delay(2000, token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                }
            }, token);
        }

        private void StopMetricsLoop()
        {
            var cts = Interlocked.Exchange(ref _metricsCts, null);
            var loop = Interlocked.Exchange(ref _metricsLoop, null);

            if (cts != null) cts.Cancel();
            if (loop != null)
            {
                try { Task.WhenAny(loop, Task.Delay(1000)).GetAwaiter().GetResult(); } catch { }
            }
            if (cts != null) cts.Dispose();
        }

        // ------------------------------------------------------------------
        //  Буфер и база
        // ------------------------------------------------------------------

        /// <summary>Кнопка «Показать размер буфера».</summary>
        public async Task<string> DescribeBuffersAsync(CancellationToken cancellationToken)
        {
            if (Engine == null) return "Принтеры не инициализированы.";

            var text = await Engine.DescribeBuffersAsync(cancellationToken).ConfigureAwait(false);

            long total = await _repository.GetTotalAsync(cancellationToken).ConfigureAwait(false);
            long issued = await _repository.GetIssuedAsync(cancellationToken).ConfigureAwait(false);

            return text + Environment.NewLine +
                   "  В базе всего: " + total + Environment.NewLine +
                   "  Выдано кодов (IsPrinted = true): " + issued;
        }

        /// <summary>
        /// Кнопка «Очистить буфер»: полностью перезаливает базу.
        /// Работает только при остановленной печати.
        /// </summary>
        public async Task<string> RefillDatabaseAsync(CancellationToken cancellationToken)
        {
            if (IsPrinting || GetRuntimes().Any(x => x.IsPrinting))
            {
                throw new InvalidOperationException("Очистка буфера доступна только при остановленной печати. " +
                                                   "Сначала нажмите «Отмена».");
            }

            Log.Info("Очистка буфера и перезаливка базы…");

            if (Engine != null && Engine.CodeSource != null)
            {
                await Engine.CodeSource.ClearAsync(cancellationToken).ConfigureAwait(false);
            }

            var result = await _repository.RefillAllAsync(
                _settings.InitialCodeCount,
                () => CodeFactory.Build(_settings.CodeFormat),
                cancellationToken).ConfigureAwait(false);

            Log.Success("Перезаливка завершена. " + result.Summary);

            // Сразу наполняем буфер, чтобы печать можно было запустить без задержки.
            if (Engine != null && Engine.CodeSource != null)
            {
                int added = await Engine.CodeSource.EnsureBufferAsync(
                    _settings.Runtime.BufferSize, 0, cancellationToken).ConfigureAwait(false);
                Log.Info("Буфер наполнен: " + added + " кодов.");
            }

            await RefreshMetricsAsync(cancellationToken).ConfigureAwait(false);
            return result.Summary;
        }

        // ------------------------------------------------------------------
        //  Завершение
        // ------------------------------------------------------------------

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try { Engine?.Dispose(); } catch { }

            StopMetricsLoop();

            try
            {
                var source = Interlocked.Exchange(ref _currentSource, null);
                if (source != null) source.Dispose();
            }
            catch { }

            try { _repository.Dispose(); } catch { }
            CodeBufferFactory.Shutdown();
        }
    }
}
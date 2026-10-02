using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LabelPrinter.Buffers;
using LabelPrinter.Codes;
using LabelPrinter.Core;
using LabelPrinter.Data;
using LabelPrinter.Protocol;

namespace LabelPrinter.Services
{
    /// <summary>Сводные показатели для строки состояния.</summary>
    public sealed class PrintMetrics
    {
        public int TotalPrinters { get; set; }
        public int ConnectedPrinters { get; set; }
        public int ActivePrinters { get; set; }
        public long TotalBufferCount { get; set; }
        public long TotalFreeCodes { get; set; }
        public long TotalIssuedCodes { get; set; }
        public long TotalPrintedThisSession { get; set; }
    }

    /// <summary>
    /// Верхний уровень: база, буферы, список принтеров и общий пуск/стоп печати.
    /// Кнопки «Печать» и «Отмена» управляют всеми принтерами сразу.
    /// </summary>
    public sealed class PrintService : IDisposable
    {
        private readonly AppSettings _settings;
        private readonly CodesRepository _repository;
        private readonly List<PrinterRuntime> _runtimes = new List<PrinterRuntime>();
        private readonly object _gate = new object();

        private CancellationTokenSource _cts;
        private Task _metricsLoop;
        private bool _disposed;

        public event EventHandler RuntimesChanged;

        public AppSettings Settings { get { return _settings; } }

        public CodesRepository Repository { get { return _repository; } }

        public PrintMetrics Metrics { get; private set; }

        public bool IsPrinting { get; private set; }

        public PrintService(AppSettings settings)
        {
            _settings = settings ?? throw new ArgumentNullException("settings");
            _settings.Normalize();

            _repository = new CodesRepository(_settings.ConnectionString);
            Metrics = new PrintMetrics();
        }

        // ------------------------------------------------------------------
        //  Запуск программы
        // ------------------------------------------------------------------

        /// <summary>
        /// Проверяет и при необходимости создаёт таблицу Codes, затем наполняет её
        /// кодами. Вызывается один раз при старте.
        /// </summary>
        public async Task<InitializationResult> InitializeDatabaseAsync(CancellationToken ct)
        {
            Log.Info("PostgreSQL: проверка таблицы «" + CodesRepository.TableName + "».");

            var result = await _repository.InitializeAsync(
                _settings.InitialCodeCount,
                GenerateCode,
                ct).ConfigureAwait(false);

            if (result.Skipped)
            {
                Log.Info("Таблица «" + CodesRepository.TableName + "» уже заполнена: всего " +
                         result.Total + ", свободно " + result.Free + ".");
            }
            else
            {
                Log.Success("База готова. " + result.Summary);
            }

            Log.Info("Генератор кодов: " + CodeFactory.Describe(_settings));

            var gtinProblem = CodeFactory.ValidateGtin14(_settings.Gtin14);
            if (gtinProblem != null)
            {
                // Не мешаем работе, но обязаны сказать: сканер может отвергнуть такие коды.
                Log.Warn("GTIN-14 «" + _settings.Gtin14 + "»: " + gtinProblem +
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

        private string GenerateCode()
        {
            return CodeFactory.Build(_settings);
        }

        // ------------------------------------------------------------------
        //  Принтеры
        // ------------------------------------------------------------------

        /// <summary>
        /// Создаёт рабочие объекты для всех принтеров из настроек и запускает
        /// фоновые циклы подключения.
        /// </summary>
        public async Task RebuildPrintersAsync(CancellationToken ct)
        {
            await ShutdownPrintersAsync().ConfigureAwait(false);

            var created = new List<PrinterRuntime>();

            foreach (var printer in _settings.Printers)
            {
                var problem = printer.Validate();
                if (problem != null)
                {
                    Log.Error("Принтер «" + printer.Name + "» пропущен: " + problem);
                    continue;
                }

                IPrinterClient client = _settings.TestPrinterMode
                    ? (IPrinterClient)new SimulatedPrinterClient(printer.Name, _settings.TestPrintIntervalSeconds)
                    : new RealPrinterClient(printer, _settings.PrinterTimeoutMs, _settings.GroupSeparatorAsEntity);

                var buffer = await CodeBufferFactory.CreateAsync(
                    _settings.RedisConnectionString,
                    _settings.BufferKey(printer.Id),
                    _settings.BufferSize,
                    ct).ConfigureAwait(false);

                var supply = new CodeSupply(_repository, buffer, _settings);
                var runtime = new PrinterRuntime(printer, client, supply, _settings);

                created.Add(runtime);
                runtime.Start();

                Log.Info("Принтер «" + printer.Name + "» " + (printer.Enabled ? "активирован" : "выключен") +
                         ": " + printer.Endpoint + ", формат «" + printer.FormatName + "», переменная «" +
                         printer.VariableName + "».");
            }

            lock (_gate)
            {
                _runtimes.Clear();
                _runtimes.AddRange(created);
            }

            if (created.Count == 0)
            {
                Log.Warn("Принтеров нет. Добавьте принтер кнопкой «Принтеры».");
            }

            StartMetricsLoop();
            await RefreshMetricsAsync(ct).ConfigureAwait(false);

            EventHandler handler = RuntimesChanged;
            if (handler != null) handler(this, EventArgs.Empty);
        }

        public IReadOnlyList<PrinterRuntime> GetRuntimes()
        {
            lock (_gate)
            {
                return _runtimes.ToList();
            }
        }

        /// <summary>Принтеры, включённые галочкой «активировать».</summary>
        public IReadOnlyList<PrinterRuntime> GetActiveRuntimes()
        {
            return GetRuntimes().Where(x => x.Printer.Enabled).ToList();
        }

        // ------------------------------------------------------------------
        //  Печать
        // ------------------------------------------------------------------

        public bool CanStartPrinting()
        {
            var active = GetActiveRuntimes();
            return active.Count > 0 && active.All(x => x.IsConnected);
        }

        /// <summary>Кнопка «Печать» — общая для всех активных принтеров.</summary>
        public async Task StartPrintingAsync(CancellationToken ct)
        {
            if (IsPrinting) return;

            var active = GetActiveRuntimes();
            if (active.Count == 0)
            {
                throw new InvalidOperationException("Нет активных принтеров. Включите хотя бы один в настройках.");
            }

            await RefreshMetricsAsync(ct).ConfigureAwait(false);

            IsPrinting = true;
            var failures = new List<string>();

            foreach (var runtime in active)
            {
                try
                {
                    await runtime.StartPrintingAsync(ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    failures.Add(runtime.Printer.Name + ": " + ex.Message);
                }
            }

            if (failures.Count > 0)
            {
                Log.Error("Запуск печати с ошибками — " + string.Join("; ", failures));
            }

            if (active.Count == failures.Count)
            {
                IsPrinting = false;
                throw new InvalidOperationException("Ни один принтер не запустился: " + string.Join("; ", failures));
            }
        }

        /// <summary>Кнопка «Отмена» — общая для всех принтеров.</summary>
        public async Task StopPrintingAsync(CancellationToken ct)
        {
            if (!IsPrinting) return;
            IsPrinting = false;

            foreach (var runtime in GetActiveRuntimes())
            {
                try
                {
                    await runtime.StopPrintingAsync(ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Log.Error("[" + runtime.Printer.Name + "] ошибка остановки: " + ex.Message);
                }
            }
        }

        // ------------------------------------------------------------------
        //  Буфер и база
        // ------------------------------------------------------------------

        /// <summary>Кнопка «Показать размер буфера»: сколько в буфере и сколько свободно в базе.</summary>
        public async Task<string> DescribeBuffersAsync(CancellationToken ct)
        {
            var lines = new List<string>();

            foreach (var runtime in GetRuntimes())
            {
                int inBuffer = await runtime.Supply.BufferCountAsync(ct).ConfigureAwait(false);
                lines.Add("  «" + runtime.Printer.Name + "» — в буфере " + inBuffer +
                          " из " + _settings.BufferSize + " (" + runtime.Supply.Buffer.SourceName + ")");
            }

            long total = await _repository.GetTotalAsync(ct).ConfigureAwait(false);
            long free = await _repository.GetFreeAsync(ct).ConfigureAwait(false);
            long issued = await _repository.GetIssuedAsync(ct).ConfigureAwait(false);

            var text = new List<string>
            {
                "Размер буфера:",
                string.Join(Environment.NewLine, lines),
                "  В базе всего: " + total,
                "  Свободно кодов в базе: " + free,
                "  Выдано кодов (IsPrinted = true): " + issued,
                "  Порог долива: " + _settings.BufferRefillThreshold
            };

            var result = string.Join(Environment.NewLine, text);
            Log.Info(result);
            return result;
        }

        /// <summary>
        /// Кнопка «Очистить буфер»: полностью перезаливает базу.
        /// Работает только при остановленной печати.
        /// </summary>
        public async Task<string> RefillDatabaseAsync(CancellationToken ct)
        {
            if (IsPrinting || GetActiveRuntimes().Any(x => x.IsPrinting))
            {
                throw new InvalidOperationException("Очистка буфера доступна только при остановленной печати. " +
                                                   "Сначала нажмите «Отмена».");
            }

            Log.Info("Очистка буфера и перезаливка базы…");

            foreach (var runtime in GetRuntimes())
            {
                int removed = await runtime.Supply.ClearBufferAsync(ct).ConfigureAwait(false);
                if (removed > 0)
                {
                    Log.Info("Буфер принтера «" + runtime.Printer.Name + "» очищен.");
                }
            }

            var result = await _repository.RefillAllAsync(
                _settings.InitialCodeCount,
                GenerateCode,
                ct).ConfigureAwait(false);

            Log.Success("Перезаливка завершена. " + result.Summary);

            // Сразу наполняем буферы, чтобы печать можно было запустить без задержки.
            foreach (var runtime in GetRuntimes())
            {
                int added = await runtime.Supply.TopUpAsync(ct).ConfigureAwait(false);
                Log.Info("Буфер принтера «" + runtime.Printer.Name + "» наполнен: " + added + " кодов.");
            }

            await RefreshMetricsAsync(ct).ConfigureAwait(false);
            return result.Summary;
        }

        // ------------------------------------------------------------------
        //  Метрики
        // ------------------------------------------------------------------

        private void StartMetricsLoop()
        {
            if (_metricsLoop != null) return;

            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            _metricsLoop = Task.Run(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        await RefreshMetricsAsync(token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch
                    {
                        // метрики не критичны
                    }

                    try
                    {
                        await Task.Delay(2000, token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }, token);
        }

        public async Task RefreshMetricsAsync(CancellationToken ct)
        {
            var runtimes = GetRuntimes();
            var metrics = new PrintMetrics
            {
                TotalPrinters = runtimes.Count,
                ConnectedPrinters = runtimes.Count(x => x.IsConnected),
                ActivePrinters = runtimes.Count(x => x.Printer.Enabled)
            };

            foreach (var runtime in runtimes)
            {
                try
                {
                    int count = await runtime.Supply.BufferCountAsync(ct).ConfigureAwait(false);
                    runtime.BufferCount = count;
                    metrics.TotalBufferCount += count;
                    metrics.TotalPrintedThisSession += runtime.PrintedThisSession;
                }
                catch
                {
                    // игнорируем — покажем прошлые значения
                }
            }

            try
            {
                long free = await _repository.GetFreeAsync(ct).ConfigureAwait(false);
                long issued = await _repository.GetIssuedAsync(ct).ConfigureAwait(false);
                metrics.TotalFreeCodes = free;
                metrics.TotalIssuedCodes = issued;

                // Показываем и на карточке принтера.
                foreach (var runtime in runtimes)
                {
                    runtime.FreeCodes = free;
                }
            }
            catch
            {
                // игнорируем
            }

            Metrics = metrics;

            foreach (var runtime in runtimes)
            {
                runtime.NotifyStateChanged();
            }
        }

        // ------------------------------------------------------------------
        //  Завершение
        // ------------------------------------------------------------------

        public async Task ShutdownPrintersAsync()
        {
            var cts = Interlocked.Exchange(ref _cts, null);
            var loop = Interlocked.Exchange(ref _metricsLoop, null);

            if (cts != null) cts.Cancel();
            if (loop != null)
            {
                try { await Task.WhenAny(loop, Task.Delay(2000)).ConfigureAwait(false); } catch { }
            }
            if (cts != null) cts.Dispose();

            List<PrinterRuntime> runtimes;
            lock (_gate)
            {
                runtimes = _runtimes.ToList();
                _runtimes.Clear();
            }

            foreach (var runtime in runtimes)
            {
                try { await runtime.StopAsync().ConfigureAwait(false); } catch { }
                runtime.Dispose();
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try { ShutdownPrintersAsync().GetAwaiter().GetResult(); } catch { }
            try { _repository.Dispose(); } catch { }
            CodeBufferFactory.Shutdown();
        }
    }
}

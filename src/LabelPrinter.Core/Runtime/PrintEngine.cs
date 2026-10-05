using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LabelPrinter.Core.Abstractions;
using LabelPrinter.Core.Configuration;
using LabelPrinter.Core.Diagnostics;
using LabelPrinter.Core.Protocol;

namespace LabelPrinter.Core.Runtime
{
    /// <summary>Сводные показатели для строки состояния.</summary>
    public sealed class PrintMetrics
    {
        public int TotalPrinters { get; set; }
        public int ConnectedPrinters { get; set; }
        public int ActivePrinters { get; set; }
        public long TotalBufferCount { get; set; }
        public long TotalFreeCodes { get; set; }
        public long TotalPrintedThisSession { get; set; }
    }

    /// <summary>
    /// Печатной движок — точка входа библиотеки.
    ///
    /// Библиотека не знает, где хранятся коды и как выглядит ваш интерфейс:
    /// достаточно реализовать <see cref="ICodeSource"/> и подписаться на журнал.
    ///
    /// Минимальный пример:
    /// <code>
    /// var engine = new PrintEngine(new PrintRuntimeSettings(), new CodeFormatSettings());
    /// engine.CodeSource = new MyCodeSource();          // откуда брать коды
    /// engine.Printers.Add(new PrinterSettings("Принтер 1", "192.168.1.10", 4100));
    ///
    /// Log.Sink = new FileLogSink(@"C:\logs");
    /// Log.Appended += (s, e) =&gt; Console.WriteLine(e.Line);
    ///
    /// await engine.StartAsync(ct);                       // подключиться
    /// await engine.StartPrintingAsync(ct);               // печатать
    /// await engine.StopPrintingAsync(ct);                // остановить
    /// </code>
    /// </summary>
    public sealed class PrintEngine : IDisposable
    {
        private readonly List<PrinterRuntime> _runtimes = new List<PrinterRuntime>();
        private readonly object _gate = new object();

        private CancellationTokenSource _cts;
        private Task _metricsLoop;
        private bool _disposed;

        public PrintEngine(PrintRuntimeSettings runtimeSettings, CodeFormatSettings codeFormat)
        {
            Runtime = runtimeSettings ?? new PrintRuntimeSettings();
            CodeFormat = codeFormat ?? new CodeFormatSettings();
            Runtime.Normalize();
            CodeFormat.Normalize();

            Printers = new List<PrinterSettings>();
            Metrics = new PrintMetrics();
        }

        public PrintRuntimeSettings Runtime { get; private set; }

        public CodeFormatSettings CodeFormat { get; private set; }

        /// <summary>
        /// Откуда берутся коды. Обязателен перед <see cref="StartAsync"/>.
        /// Реализация может использовать что угодно: БД, файл, очередь сообщений.
        /// </summary>
        public ICodeSource CodeSource { get; set; }

        /// <summary>Принтеры, которые нужно подключить и обслуживать.</summary>
        public IList<PrinterSettings> Printers { get; private set; }

        public PrintMetrics Metrics { get; private set; }

        public bool IsPrinting { get; private set; }

        /// <summary>Состав принтеров изменился — интерфейсу пора перерисоваться.</summary>
        public event EventHandler StateChanged;

        public IReadOnlyList<PrinterRuntime> Runtimes
        {
            get { lock (_gate) { return _runtimes.ToList(); } }
        }

        /// <summary>Принтеры, включённые галочкой «активировать».</summary>
        public IReadOnlyList<PrinterRuntime> ActiveRuntimes
        {
            get { return Runtimes.Where(x => x.Printer.Enabled).ToList(); }
        }

        // ------------------------------------------------------------------
        //  Подключение
        // ------------------------------------------------------------------

        /// <summary>
        /// Создаёт рабочие объекты для всех принтеров и запускает фоновые циклы
        /// подключения. Идемпотентно: повторный вызов пересоздаёт соединения.
        /// </summary>
        public async Task StartAsync(CancellationToken cancellationToken)
        {
            if (CodeSource == null)
            {
                throw new InvalidOperationException(
                    "Не задан источник кодов. Присвойте PrintEngine.CodeSource перед запуском.");
            }

            await ShutdownAsync().ConfigureAwait(false);

            var created = new List<PrinterRuntime>();

            foreach (var printer in Printers)
            {
                var problem = printer.Validate();
                if (problem != null)
                {
                    Log.Error("Принтер «" + printer.Name + "» пропущен: " + problem);
                    continue;
                }

                IPrinterClient client = Runtime.TestPrinterMode
                    ? (IPrinterClient)new SimulatedPrinterClient(printer.Name, Runtime.TestPrintIntervalSeconds)
                    : new RealPrinterClient(printer, Runtime.PrinterTimeoutMs, CodeFormat.GroupSeparatorAsEntity);

                var runtime = new PrinterRuntime(printer, client, CodeSource, Runtime, CodeFormat);
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
                Log.Warn("Принтеров нет. Добавьте принтер в PrintEngine.Printers.");
            }

            StartMetricsLoop();
            await RefreshMetricsAsync(cancellationToken).ConfigureAwait(false);

            var handler = StateChanged;
            if (handler != null) handler(this, EventArgs.Empty);
        }

        // ------------------------------------------------------------------
        //  Печать
        // ------------------------------------------------------------------

        /// <summary>Пуск печати на всех активных принтерах.</summary>
        public async Task StartPrintingAsync(CancellationToken cancellationToken)
        {
            if (IsPrinting) return;

            var active = ActiveRuntimes;
            if (active.Count == 0)
            {
                throw new InvalidOperationException("Нет активных принтеров. Включите хотя бы один.");
            }

            await RefreshMetricsAsync(cancellationToken).ConfigureAwait(false);

            IsPrinting = true;
            var failures = new List<string>();

            foreach (var runtime in active)
            {
                try
                {
                    await runtime.StartPrintingAsync(cancellationToken).ConfigureAwait(false);
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

        /// <summary>Остановка печати на всех активных принтерах.</summary>
        public async Task StopPrintingAsync(CancellationToken cancellationToken)
        {
            if (!IsPrinting) return;
            IsPrinting = false;

            foreach (var runtime in ActiveRuntimes)
            {
                try
                {
                    await runtime.StopPrintingAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Log.Error("[" + runtime.Printer.Name + "] ошибка остановки: " + ex.Message);
                }
            }
        }

        // ------------------------------------------------------------------
        //  Показатели
        // ------------------------------------------------------------------

        public async Task RefreshMetricsAsync(CancellationToken cancellationToken)
        {
            var runtimes = Runtimes;
            var metrics = new PrintMetrics
            {
                TotalPrinters = runtimes.Count,
                ConnectedPrinters = runtimes.Count(x => x.IsConnected),
                ActivePrinters = runtimes.Count(x => x.Printer.Enabled)
            };

            long remaining = 0;
            try
            {
                remaining = await CodeSource.GetRemainingAsync(cancellationToken).ConfigureAwait(false);
                metrics.TotalFreeCodes = remaining;
            }
            catch
            {
                // показатели не критичны
            }

            foreach (var runtime in runtimes)
            {
                try
                {
                    int count = await CodeSource.GetBufferCountAsync(cancellationToken).ConfigureAwait(false);
                    runtime.BufferCount = count;
                    runtime.FreeCodes = remaining;

                    metrics.TotalBufferCount += count;
                    metrics.TotalPrintedThisSession += runtime.PrintedThisSession;
                }
                catch
                {
                    // покажем прошлые значения
                }
            }

            Metrics = metrics;

            foreach (var runtime in runtimes)
            {
                runtime.NotifyStateChanged();
            }
        }

        /// <summary>Готовый отчёт о буферах и остатке кодов — для кнопки «Показать размер буфера».</summary>
        public async Task<string> DescribeBuffersAsync(CancellationToken cancellationToken)
        {
            var lines = new List<string>();
            long remaining = 0;

            foreach (var runtime in Runtimes)
            {
                int inBuffer = 0;
                try
                {
                    inBuffer = await CodeSource.GetBufferCountAsync(cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    // игнорируем
                }
                lines.Add("  «" + runtime.Printer.Name + "» — в буфере " + inBuffer +
                          " из " + Runtime.BufferSize);
            }

            try
            {
                remaining = await CodeSource.GetRemainingAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // игнорируем
            }

            var text = new List<string>
            {
                "Размер буфера:",
                string.Join(Environment.NewLine, lines),
                "  Свободно кодов в хранилище: " + remaining,
                "  Порог долива: " + Runtime.BufferRefillThreshold
            };

            var result = string.Join(Environment.NewLine, text);
            Log.Info(result);
            return result;
        }

        // ------------------------------------------------------------------
        //  Завершение
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
                    try { await RefreshMetricsAsync(token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                    catch { /* показатели не критичны */ }

                    try { await Task.Delay(2000, token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                }
            }, token);
        }

        /// <summary>Останавливает печать, гасит фоновые циклы и закрывает соединения.</summary>
        public async Task ShutdownAsync()
        {
            if (IsPrinting)
            {
                try { await StopPrintingAsync(CancellationToken.None).ConfigureAwait(false); }
                catch { }
            }

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

            try { ShutdownAsync().GetAwaiter().GetResult(); } catch { }
        }
    }
}
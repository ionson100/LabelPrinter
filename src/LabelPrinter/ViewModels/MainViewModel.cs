using System;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using LabelPrinter.Core.Codes;
using LabelPrinter.Core.Diagnostics;
using LabelPrinter.Services;
using LabelPrinter.Configuration;
using LabelPrinter.Core.Runtime;
using LabelPrinter.Infrastructure;

namespace LabelPrinter.ViewModels
{
    /// <summary>
    /// Главная модель представления: кнопки, принтеры, показатели и запуск печати.
    /// Все долгие операции выполняются в фоне, результат попадает в журнал.
    /// </summary>
    public sealed class MainViewModel : ObservableObject, IDisposable
    {
        private readonly PrintService _printService;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private bool _disposed;

        private bool _isPrinting;
        private bool _isBusy;
        private string _busyText;
        private string _statusMessage = "Готово";
        private string _bufferInfo = "—";

        private int _bufferSize;
        private int _bufferThreshold;
        private int _pollIntervalMs;
        private int _testIntervalSeconds;
        private bool _testPrinterMode;
        private int _initialCodeCount;

        private long _metricBuffer;
        private long _metricFree;
        private long _metricIssued;
        private long _metricPrinted;
        private string _codeSample = "—";

        public MainViewModel(PrintService printService)
        {
            _printService = printService ?? throw new ArgumentNullException("printService");
            var settings = _printService.Settings;

            _bufferSize = settings.BufferSize;
            _bufferThreshold = settings.BufferRefillThreshold;
            _pollIntervalMs = settings.PollIntervalMs;
            _testIntervalSeconds = settings.TestPrintIntervalSeconds;
            _testPrinterMode = settings.TestPrinterMode;
            _initialCodeCount = settings.InitialCodeCount;

            Printers = new ObservableCollection<PrinterRuntime>();
            _printService.StateChanged += OnRuntimesChanged;

            PrintCommand = new RelayCommand(async () => await TogglePrintingAsync(), () => !IsBusy);
            PrintersCommand = new RelayCommand(OpenPrintersWindow);
            ShowBufferCommand = new RelayCommand(async () => await ShowBufferAsync(), () => !IsBusy);
            ClearBufferCommand = new RelayCommand(async () => await ClearBufferAsync(), () => !IsBusy && !IsPrinting);
            ApplySettingsCommand = new RelayCommand(async () => await ApplySettingsAsync(), () => !IsBusy && !IsPrinting);
            ReconnectCommand = new RelayCommand(async () => await ReconnectAsync(), () => !IsBusy);

            _ = PollMetricsAsync();
        }

        // ------------------------------------------------------------------
        //  Команды
        // ------------------------------------------------------------------

        public RelayCommand PrintCommand { get; private set; }

        public RelayCommand PrintersCommand { get; private set; }

        public RelayCommand ShowBufferCommand { get; private set; }

        public RelayCommand ClearBufferCommand { get; private set; }

        public RelayCommand ApplySettingsCommand { get; private set; }

        public RelayCommand ReconnectCommand { get; private set; }

        // ------------------------------------------------------------------
        //  Состояние
        // ------------------------------------------------------------------

        public ObservableCollection<PrinterRuntime> Printers { get; private set; }

        public bool IsPrinting
        {
            get { return _isPrinting; }
            private set
            {
                if (Set(ref _isPrinting, value))
                {
                    Raise("PrintButtonText");
                    Raise("IsPrintRunning");
                }
            }
        }

        public bool IsPrintRunning { get { return _isPrinting; } }

        public string PrintButtonText { get { return _isPrinting ? "Отмена" : "Печать"; } }

        public bool IsBusy
        {
            get { return _isBusy; }
            private set
            {
                if (Set(ref _isBusy, value)) RelayCommand.Invalidate();
            }
        }

        public string BusyText
        {
            get { return _busyText; }
            private set { Set(ref _busyText, value); }
        }

        public string StatusMessage
        {
            get { return _statusMessage; }
            private set { Set(ref _statusMessage, value); }
        }

        public string BufferInfo
        {
            get { return _bufferInfo; }
            private set { Set(ref _bufferInfo, value); }
        }

        // ---- редактируемые настройки ----

        public int BufferSize
        {
            get { return _bufferSize; }
            set { Set(ref _bufferSize, value); }
        }

        public int BufferThreshold
        {
            get { return _bufferThreshold; }
            set { Set(ref _bufferThreshold, value); }
        }

        /// <summary>Интервал опроса счётчика напечатанных этикеток, мс.</summary>
        public int PollIntervalMs
        {
            get { return _pollIntervalMs; }
            set { Set(ref _pollIntervalMs, value); }
        }

        public int TestIntervalSeconds
        {
            get { return _testIntervalSeconds; }
            set { Set(ref _testIntervalSeconds, value); }
        }

        public bool TestPrinterMode
        {
            get { return _testPrinterMode; }
            set { Set(ref _testPrinterMode, value); }
        }

        public int InitialCodeCount
        {
            get { return _initialCodeCount; }
            set { Set(ref _initialCodeCount, value); }
        }

        // ---- метрики ----

        public long MetricBuffer
        {
            get { return _metricBuffer; }
            private set { Set(ref _metricBuffer, value); }
        }

        public long MetricFree
        {
            get { return _metricFree; }
            private set { Set(ref _metricFree, value); }
        }

        public long MetricIssued
        {
            get { return _metricIssued; }
            private set { Set(ref _metricIssued, value); }
        }

        public long MetricPrinted
        {
            get { return _metricPrinted; }
            private set { Set(ref _metricPrinted, value); }
        }

        /// <summary>Живой пример кода по текущим настройкам — сразу видно формат.</summary>
        public string CodeSample
        {
            get { return _codeSample; }
            private set { Set(ref _codeSample, value); }
        }

        // ------------------------------------------------------------------
        //  Действия
        // ------------------------------------------------------------------

        /// <summary>Старт программы: проверка таблицы, наполнение кодами, подключение принтеров.</summary>
        public async Task StartupAsync()
        {
            IsBusy = true;
            BusyText = "Проверка базы данных…";
            StatusMessage = "Подключение к PostgreSQL…";

            RefreshCodeSample();

            try
            {
                var result = await _printService.InitializeDatabaseAsync(_cts.Token);
                StatusMessage = result.Summary;
            }
            catch (Exception ex)
            {
                StatusMessage = "Ошибка базы данных: " + ex.Message;
                Log.Error("Не удалось подготовить базу данных.", ex);
                MessageBox.Show(
                    "Не удалось подготовить базу данных.\r\n\r\n" + ex.Message +
                    "\r\n\r\nПроверьте, что PostgreSQL запущен, и строку соединения в настройках.",
                    "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
                IsBusy = false;
                BusyText = null;
                return;
            }

            BusyText = "Подключение к принтерам…";
            StatusMessage = "Подключение к принтерам…";

            try
            {
                await _printService.RebuildPrintersAsync(_cts.Token);
                StatusMessage = "Готово к работе.";
            }
            catch (Exception ex)
            {
                StatusMessage = "Ошибка подключения: " + ex.Message;
                Log.Error("Не удалось подключить принтеры.", ex);
            }

            IsBusy = false;
            BusyText = null;
        }

        private void RefreshCodeSample()
        {
            try
            {
                var code = CodeFactory.Build(_printService.Settings.CodeFormat);
                CodeSample = CodeFactory.ToHumanReadable(code);
            }
            catch
            {
                CodeSample = "—";
            }
        }

        private async Task TogglePrintingAsync()
        {
            if (IsPrinting)
            {
                await RunGuardedAsync("Остановка печати…", async () =>
                {
                    await _printService.StopPrintingAsync(_cts.Token);
                    IsPrinting = false;
                    StatusMessage = "Печать остановлена.";
                });
                return;
            }

            bool anyEnabled = false;
            foreach (var runtime in Printers)
            {
                if (runtime.Printer.Enabled) { anyEnabled = true; break; }
            }

            if (!anyEnabled)
            {
                MessageBox.Show(
                    "Нет активных принтеров. Откройте «Принтеры» и включите хотя бы один галочкой.",
                    "Принтеры", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            await RunGuardedAsync("Запуск печати…", async () =>
            {
                await _printService.StartPrintingAsync(_cts.Token);
                IsPrinting = true;
                StatusMessage = "Печать идёт.";
            });
        }

        private async Task ShowBufferAsync()
        {
            await RunGuardedAsync("Опрос буфера…", async () =>
            {
                BufferInfo = await _printService.DescribeBuffersAsync(_cts.Token);
            });
        }

        private async Task ClearBufferAsync()
        {
            if (IsPrinting)
            {
                MessageBox.Show(
                    "Очистка буфера работает только при остановленной печати.\r\nСначала нажмите «Отмена».",
                    "Печать идёт", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var answer = MessageBox.Show(
                "Перезалить базу кодов?\r\n\r\n" +
                "• буферы будут очищены;\r\n" +
                "• все коды снова станут свободными;\r\n" +
                "• база будет дополнена до " + _printService.Settings.InitialCodeCount + " шт.\r\n\r\n" +
                "Уже напечатанные этикетки это не отменит.",
                "Очистить буфер", MessageBoxButton.OKCancel, MessageBoxImage.Question);

            if (answer != MessageBoxResult.OK) return;

            await RunGuardedAsync("Перезаливка базы…", async () =>
            {
                BufferInfo = await _printService.RefillDatabaseAsync(_cts.Token);
                StatusMessage = "База перезалита.";
            });
        }

        /// <summary>Применяет изменённые настройки буфера и режима печати.</summary>
        private async Task ApplySettingsAsync()
        {
            await RunGuardedAsync("Применение настроек…", async () =>
            {
                var settings = _printService.Settings;
                settings.BufferSize = BufferSize;
                settings.BufferRefillThreshold = BufferThreshold;
                settings.PollIntervalMs = PollIntervalMs;
                settings.TestPrintIntervalSeconds = TestIntervalSeconds;
                settings.TestPrinterMode = TestPrinterMode;
                settings.InitialCodeCount = InitialCodeCount;
                settings.Normalize();

                // Normalize() мог поправить значения — показываем то, что реально применится.
                BufferSize = settings.BufferSize;
                BufferThreshold = settings.BufferRefillThreshold;
                PollIntervalMs = settings.PollIntervalMs;
                TestIntervalSeconds = settings.TestPrintIntervalSeconds;
                InitialCodeCount = settings.InitialCodeCount;

                SettingsService.Save(settings);

                Log.Info("Настройки применены: буфер " + settings.BufferSize + ", порог долива " +
                         settings.BufferRefillThreshold + ", опрос счётчика " + settings.PollIntervalMs + " мс, режим «" +
                         (settings.TestPrinterMode ? "тестовый принтер" : "реальный принтер") +
                         "», интервал теста " + settings.TestPrintIntervalSeconds + " с.");

                await _printService.RebuildPrintersAsync(_cts.Token);
                StatusMessage = "Настройки применены.";
            });
        }

        private async Task ReconnectAsync()
        {
            await RunGuardedAsync("Переподключение…", async () =>
            {
                await _printService.RebuildPrintersAsync(_cts.Token);
                StatusMessage = "Переподключение выполнено.";
            });
        }

        private void OpenPrintersWindow()
        {
            var window = new Views.PrintersWindow(_printService.Settings);
            window.Owner = Application.Current.MainWindow;
            window.ShowDialog();

            if (!window.Saved) return;

            SettingsService.Save(_printService.Settings);
            Log.Info("Список принтеров сохранён (" + _printService.Settings.Printers.Count + " шт.).");

            Task.Run(async () =>
            {
                try
                {
                    await _printService.RebuildPrintersAsync(CancellationToken.None);
                    RaiseOnUi(() => StatusMessage = "Список принтеров обновлён.");
                }
                catch (Exception ex)
                {
                    Log.Error("Не удалось применить список принтеров.", ex);
                }
            });
        }

        // ------------------------------------------------------------------
        //  Вспомогательное
        // ------------------------------------------------------------------

        private async Task RunGuardedAsync(string busyText, Func<Task> action)
        {
            IsBusy = true;
            BusyText = busyText;
            StatusMessage = busyText;

            try
            {
                await action();
            }
            catch (OperationCanceledException)
            {
                // закрытие программы
            }
            catch (Exception ex)
            {
                StatusMessage = "Ошибка: " + ex.Message;
                Log.Error(busyText.TrimEnd('…') + " — ошибка", ex);
                MessageBox.Show(ex.Message, "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally
            {
                IsBusy = false;
                BusyText = null;
                RelayCommand.Invalidate();
            }
        }

        private void OnRuntimesChanged(object sender, EventArgs e)
        {
            RaiseOnUi(() =>
            {
                Printers.Clear();
                foreach (var runtime in _printService.GetRuntimes())
                {
                    Printers.Add(runtime);
                }
                RelayCommand.Invalidate();
            });
        }

        private async Task PollMetricsAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    var metrics = _printService.Metrics;
                    RaiseOnUi(() =>
                    {
                        MetricBuffer = metrics.TotalBufferCount;
                        MetricFree = metrics.TotalFreeCodes;
                        MetricIssued = _printService.IssuedCodes;
                        MetricPrinted = metrics.TotalPrintedThisSession;
                    });
                }
                catch
                {
                    // метрики не критичны
                }

                try
                {
                    await Task.Delay(1000, _cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        /// <summary>Пересчёт свойств идёт в UI-потоке, а вызывать его могут фоновые задачи.</summary>
        private static void RaiseOnUi(Action action)
        {
            var dispatcher = Application.Current == null ? null : Application.Current.Dispatcher;

            if (dispatcher == null || dispatcher.CheckAccess())
            {
                action();
                return;
            }

            dispatcher.BeginInvoke(new Action(action));
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try { _cts.Cancel(); } catch { }
            _printService.StateChanged -= OnRuntimesChanged;
        }
    }
}

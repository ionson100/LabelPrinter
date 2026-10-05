using System;
using System.Text;
using System.Windows;
using LabelPrinter.Core.Diagnostics;
using LabelPrinter.ViewModels;

namespace LabelPrinter.Views
{
    /// <summary>
    /// Главное окно: принтеры, кнопки управления, показатели и журнал.
    ///
    /// Журнал выводится в обычный TextBox белым по чёрному и сам прокручивается
    /// вниз при поступлении новых строк.
    /// </summary>
    public partial class MainWindow : Window
    {
        private readonly MainViewModel _viewModel;
        private bool _logHooked;

        public MainWindow()
        {
            InitializeComponent();
        }

        public MainWindow(MainViewModel viewModel)
            : this()
        {
            _viewModel = viewModel;
            DataContext = _viewModel;
            Loaded += OnWindowLoaded;
        }

        /// <summary>
        /// Стартовые операции (проверка таблицы, наполнение кодами, подключение
        /// принтеров) занимают время — выполняем их после показа окна,
        /// чтобы интерфейс не «висел» на белом экране.
        /// </summary>
        private async void OnWindowLoaded(object sender, RoutedEventArgs e)
        {
            Loaded -= OnWindowLoaded;

            HookLog();

            if (_viewModel == null) return;

            await _viewModel.StartupAsync();
        }

        protected override void OnClosed(EventArgs e)
        {
            UnhookLog();

            try
            {
                // Печать должна быть корректно остановлена, принтер — переведён на паузу.
                // Dispose идемпотентен и отменяет фоновые задачи.
                _viewModel?.Dispose();
            }
            catch
            {
                // завершение работы — исключения игнорируем
            }

            base.OnClosed(e);
        }

        // ------------------------------------------------------------------
        //  Журнал
        // ------------------------------------------------------------------

        private void HookLog()
        {
            if (_logHooked) return;

            Log.Appended += OnLogAppended;
            _logHooked = true;
        }

        private void UnhookLog()
        {
            if (!_logHooked) return;
            Log.Appended -= OnLogAppended;
            _logHooked = false;
        }

        private void OnLogAppended(object sender, LogMessage entry)
        {
            // Журнал пишется из фоновых задач — в UI-поток попадаем через Dispatcher.
            if (Dispatcher.CheckAccess())
            {
                AppendLine(entry.Line);
                return;
            }

            Dispatcher.BeginInvoke(new Action(() => AppendLine(entry.Line)));
        }

        private void AppendLine(string line)
        {
            LogBox.AppendText(line + Environment.NewLine);

            // Ограничиваем размер текстового поля, иначе расход памяти растёт бесконечно.
            if (LogBox.Text.Length > 400000)
            {
                var trimmed = LogBox.Text.Substring(LogBox.Text.Length / 2);
                int start = trimmed.IndexOf('\n');
                LogBox.Text = start >= 0 ? trimmed.Substring(start + 1) : trimmed;
            }

            LogBox.ScrollToEnd();
        }

        private void OnClearLogClick(object sender, RoutedEventArgs e)
        {
            LogBox.Clear();
            Log.Info("Журнал очищен.");
        }
    }
}
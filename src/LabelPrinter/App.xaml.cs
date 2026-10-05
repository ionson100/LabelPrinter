using System;
using System.Windows;
using System.Windows.Threading;
using LabelPrinter.Core.Diagnostics;
using LabelPrinter.Services;
using LabelPrinter.ViewModels;
using LabelPrinter.Views;
using LabelPrinter.Infrastructure;
using LabelPrinter.Configuration;

namespace LabelPrinter
{
    public partial class App : Application
    {
        private PrintService _printService;
        private MainViewModel _viewModel;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            AppPaths.EnsureCreated();

            // Журнал библиотеки по умолчанию молчит — приёмник подставляет
            // приложение. Без этой строки записи шли бы только в окно,
            // а на диск не попадали бы.
            Log.Sink = new FileLogSink(AppPaths.LogDir);

            DispatcherUnhandledException += OnUnhandledException;

            Log.Info("Запуск LabelPrinter. Рабочие файлы: " + AppPaths.Root);
            Log.Info("Протокол принтера: APLINK XML (MRX72e / HRX), порт по умолчанию 4100.");

            // Настройки читаем до создания окна: модель представления сразу
            // показывает пользователю реальные значения буфера и режима печати.
            var settings = SettingsService.Load();
            _printService = new PrintService(settings);
            _viewModel = new MainViewModel(_printService);

            var window = new MainWindow(_viewModel);
            MainWindow = window;
            window.Show();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            try
            {
                _viewModel?.Dispose();
                _printService?.Dispose();
            }
            catch (Exception ex)
            {
                Log.Error("Ошибка при завершении работы.", ex);
            }

            base.OnExit(e);
        }

        private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            Log.Error("Непредвиденная ошибка в интерфейсе", e.Exception);

            MessageBox.Show(
                "Произошла непредвиденная ошибка:\r\n\r\n" + e.Exception.Message +
                "\r\n\r\nПодробности записаны в журнал:\r\n" + AppPaths.LogFile,
                "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);

            // Не роняем программу: фоновые задачи печати продолжают работать.
            e.Handled = true;
        }
    }
}

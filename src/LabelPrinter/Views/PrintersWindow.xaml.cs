using System.Windows;

namespace LabelPrinter.Views
{
    /// <summary>Окно настройки принтеров: добавление, удаление, активация.</summary>
    public partial class PrintersWindow : Window
    {
        /// <summary>true, если пользователь нажал «Сохранить».</summary>
        public bool Saved { get; private set; }

        public PrintersWindow()
        {
            InitializeComponent();
        }

        public PrintersWindow(Core.AppSettings settings)
            : this()
        {
            DataContext = new ViewModels.PrintersViewModel(settings);
        }

        private void OnSaveClick(object sender, RoutedEventArgs e)
        {
            var vm = DataContext as ViewModels.PrintersViewModel;
            if (vm != null && !vm.TryCommit())
            {
                return;   // валидация не прошла, окно остаётся открытым
            }

            Saved = true;
            Close();
        }

        private void OnCancelClick(object sender, RoutedEventArgs e)
        {
            Saved = false;
            Close();
        }
    }
}

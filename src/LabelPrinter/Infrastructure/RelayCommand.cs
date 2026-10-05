using System;
using System.Windows.Input;

namespace LabelPrinter.Infrastructure
{
    /// <summary>Простая реализация ICommand без внешних зависимостей.</summary>
    public sealed class RelayCommand : ICommand
    {
        private readonly Action<object> _execute;
        private readonly Func<object, bool> _canExecute;

        public RelayCommand(Action<object> execute) : this(execute, null) { }

        public RelayCommand(Action<object> execute, Func<object, bool> canExecute)
        {
            if (execute == null) throw new ArgumentNullException("execute");
            _execute = execute;
            _canExecute = canExecute;
        }

        public RelayCommand(Action execute) : this(o => execute(), null) { }

        public RelayCommand(Action execute, Func<bool> canExecute)
            : this(o => execute(), canExecute == null ? (Func<object, bool>)null : o => canExecute())
        {
        }

        public event EventHandler CanExecuteChanged
        {
            add { CommandManager.RequerySuggested += value; }
            remove { CommandManager.RequerySuggested -= value; }
        }

        public bool CanExecute(object parameter)
        {
            return _canExecute == null || _canExecute(parameter);
        }

        public void Execute(object parameter)
        {
            _execute(parameter);
        }

        /// <summary>Попросить WPF пересчитать доступность кнопок.</summary>
        public static void Invalidate()
        {
            CommandManager.InvalidateRequerySuggested();
        }
    }
}

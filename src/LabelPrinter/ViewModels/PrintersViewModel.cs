using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using LabelPrinter.Core.Diagnostics;
using LabelPrinter.Configuration;
using LabelPrinter.Core.Configuration;
using LabelPrinter.Infrastructure;

namespace LabelPrinter.ViewModels
{
    /// <summary>Редактирование списка принтеров: добавить, удалить, активировать.</summary>
    public sealed class PrintersViewModel : ObservableObject
    {
        private readonly AppSettings _settings;
        private readonly ObservableCollection<PrinterSettings> _working;
        private PrinterSettings _selected;
        private string _validationMessage;

        public PrintersViewModel(AppSettings settings)
        {
            _settings = settings ?? throw new ArgumentNullException("settings");

            // Работаем с копиями: «Отмена» не должна испортить настройки.
            _working = new ObservableCollection<PrinterSettings>(
                settings.Printers.Select(x => x.Clone()).ToList());

            if (_working.Count == 0) _working.Add(new PrinterSettings());
            _selected = _working[0];

            AddCommand = new RelayCommand(Add);
            RemoveCommand = new RelayCommand(Remove, () => Selected != null);
            DuplicateCommand = new RelayCommand(Duplicate, () => Selected != null);
        }

        public ObservableCollection<PrinterSettings> Printers { get { return _working; } }

        public PrinterSettings Selected
        {
            get { return _selected; }
            set { Set(ref _selected, value); RelayCommand.Invalidate(); }
        }

        public string ValidationMessage
        {
            get { return _validationMessage; }
            private set
            {
                Set(ref _validationMessage, value);
                Raise("HasValidationMessage");
            }
        }

        public bool HasValidationMessage
        {
            get { return !string.IsNullOrEmpty(_validationMessage); }
        }

        public RelayCommand AddCommand { get; private set; }

        public RelayCommand RemoveCommand { get; private set; }

        public RelayCommand DuplicateCommand { get; private set; }

        private void Add()
        {
            var printer = new PrinterSettings { Name = "Принтер " + (_working.Count + 1) };
            _working.Add(printer);
            Selected = printer;
        }

        private void Remove()
        {
            if (Selected == null) return;

            _working.Remove(Selected);
            Selected = _working.Count > 0 ? _working[_working.Count - 1] : null;
        }

        private void Duplicate()
        {
            if (Selected == null) return;

            var copy = Selected.Clone();
            copy.Id = Guid.NewGuid().ToString("N");
            copy.Name = Selected.Name + " (копия)";

            int index = _working.IndexOf(Selected);
            _working.Insert(index + 1, copy);
            Selected = copy;
        }

        /// <summary>
        /// Проверяет список и, если всё в порядке, переносит правки в настройки.
        /// Возвращает false, если есть ошибки — окно останется открытым.
        /// </summary>
        public bool TryCommit()
        {
            foreach (var printer in _working)
            {
                var problem = printer.Validate();
                if (problem != null)
                {
                    ValidationMessage = "Принтер «" + (printer.Name ?? "?") + "»: " + problem;
                    Selected = printer;
                    return false;
                }
            }

            // Копии в рабочем списке — переносим в настройки те же объекты,
            // чтобы ссылки на них в UI не «отвалились».
            _settings.Printers.Clear();
            foreach (var printer in _working)
            {
                _settings.Printers.Add(printer);
            }
            _settings.Normalize();

            ValidationMessage = null;
            return true;
        }
    }
}

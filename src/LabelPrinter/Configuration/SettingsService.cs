using System;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using LabelPrinter.Core.Configuration;
using LabelPrinter.Core.Diagnostics;
using LabelPrinter.Infrastructure;

namespace LabelPrinter.Configuration
{
    /// <summary>Чтение и запись settings.json. Запись атомарная через временный файл.</summary>
    public static class SettingsService
    {
        public static AppSettings Load()
        {
            AppPaths.EnsureCreated();

            bool firstRun = !File.Exists(AppPaths.SettingsFile);

            if (firstRun) return CreateDefault();

            try
            {
                var json = File.ReadAllText(AppPaths.SettingsFile, Encoding.UTF8);
                var settings = JsonConvert.DeserializeObject<AppSettings>(json);
                if (settings == null) return CreateDefault();
                settings.Normalize();
                return settings;
            }
            catch (Exception ex)
            {
                // Битый файл настроек не должен ронять программу: отводим его в сторону.
                try
                {
                    var broken = AppPaths.SettingsFile + ".broken-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
                    File.Move(AppPaths.SettingsFile, broken);
                }
                catch
                {
                    // если и переименовать не вышло — просто продолжаем с настройками по умолчанию
                }

                Log.Warn("Не удалось прочитать settings.json (" + ex.Message + "). Взяты настройки по умолчанию.");
                return CreateDefault();
            }
        }

        /// <summary>
        /// Настройки по умолчанию с одним принтером-заготовкой, чтобы на первом
        /// запуске список не был пустым: остаётся только вписать IP-адрес.
        /// </summary>
        private static AppSettings CreateDefault()
        {
            var settings = new AppSettings();
            settings.Printers.Add(new PrinterSettings
            {
                Name = "Принтер 1",
                Host = "192.168.1.10",
                Port = 4100,
                Enabled = true,
                FormatName = "demo",
                VariableName = "code"
            });

            Log.Info("Созданы настройки по умолчанию: " + AppPaths.SettingsFile);
            Save(settings);
            return settings;
        }

        public static bool Save(AppSettings settings)
        {
            try
            {
                AppPaths.EnsureCreated();
                var json = JsonConvert.SerializeObject(settings, Formatting.Indented);
                var tmp = AppPaths.SettingsFile + ".tmp";
                File.WriteAllText(tmp, json, Encoding.UTF8);
                if (File.Exists(AppPaths.SettingsFile)) File.Delete(AppPaths.SettingsFile);
                File.Move(tmp, AppPaths.SettingsFile);
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("Не удалось сохранить настройки: " + ex.Message);
                return false;
            }
        }
    }
}

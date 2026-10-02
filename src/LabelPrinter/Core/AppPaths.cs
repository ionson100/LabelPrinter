using System;
using System.IO;

namespace LabelPrinter.Core
{
    /// <summary>Рабочие пути приложения: настройки и журналы в %AppData%\LabelPrinter.</summary>
    public static class AppPaths
    {
        private static readonly object Gate = new object();

        public static string Root
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "LabelPrinter");
            }
        }

        public static string SettingsFile { get { return Path.Combine(Root, "settings.json"); } }

        public static string LogDir { get { return Path.Combine(Root, "logs"); } }

        /// <summary>Журнал с ротацией по месяцам.</summary>
        public static string LogFile
        {
            get { return Path.Combine(LogDir, "labelprinter-" + DateTime.Now.ToString("yyyy-MM") + ".log"); }
        }

        public static void EnsureCreated()
        {
            lock (Gate)
            {
                if (!Directory.Exists(Root)) Directory.CreateDirectory(Root);
                if (!Directory.Exists(LogDir)) Directory.CreateDirectory(LogDir);
            }
        }
    }
}

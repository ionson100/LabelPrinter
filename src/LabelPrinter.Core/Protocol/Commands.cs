using System;
using System.Collections.Generic;
using System.Text;

namespace LabelPrinter.Core.Protocol
{
    /// <summary>
    /// Тексты команд XML-протокола APLINK.
    /// Синтаксис — по «APLINK SERIES: XML PROTOCOL MANUAL 2.24».
    ///
    /// Правила протокола, которые здесь соблюдаются:
    ///  * за раз разрешено одно сообщение, обёрнутое в &lt;PROTOCOL&gt; … &lt;/PROTOCOL&gt;;
    ///  * за один обмен принтер отвечает ровно один раз.
    /// </summary>
    public static class Commands
    {
        /// <summary>Оборачивает тело команды в &lt;PROTOCOL&gt;.</summary>
        public static string Wrap(string body)
        {
            return "<PROTOCOL>" + Environment.NewLine + body + Environment.NewLine + "</PROTOCOL>";
        }

        /// <summary>Количество напечатанных этикеток (общий счётчик и счётчик задания).</summary>
        public static string GetCounter()
        {
            return Wrap("<GET_COUNTER />");
        }

        /// <summary>Готов ли принтер печатать.</summary>
        public static string GetPrintingStatus()
        {
            return Wrap("<GET_PRINTING_STATUS />");
        }

        /// <summary>Имена форматов, загруженных в принтер.</summary>
        public static string GetFormatList()
        {
            return Wrap("<GET_FORMAT_LIST />");
        }

        /// <summary>Активные флаги ошибок.</summary>
        public static string GetErrorList()
        {
            return Wrap("<GET_ERROR_LIST />");
        }

        /// <summary>Модель, серийный номер и версии.</summary>
        public static string GetPrinterInfo()
        {
            return Wrap("<GET_PRINTER_INFO />");
        }

        /// <summary>Параметры печатающей головы.</summary>
        public static string GetPrintConfig()
        {
            return Wrap("<GET_PRINT_CONFIG />");
        }

        /// <summary>Версии программного обеспечения принтера.</summary>
        public static string GetVersions()
        {
            return Wrap("<GET_VERSIONS />");
        }

        /// <summary>Состояние очереди печати печатающей головы.</summary>
        public static string QueueStatus(int boardId)
        {
            return Wrap("<QUEUE_STATUS BoardId=" + boardId + " />");
        }

        public static string QueueEnable()
        {
            return Wrap("<QUEUE_ENABLE />");
        }

        public static string QueueDisable()
        {
            return Wrap("<QUEUE_DISABLE />");
        }

        public static string QueueClear()
        {
            return Wrap("<QUEUE_CLEAR />");
        }

        /// <summary>
        /// SET_PRINTING_FORMAT — ключевая команда: загружает этикетку, подставляет
        /// значения в её переменные и ставит задание в печать.
        ///
        /// Передаётся сразу несколько переменных: часть полей этикетки одинакова
        /// на всём тираже (дата, вес, GLEI), часть меняется в каждой строке
        /// (например серийный номер). Порядок переменных сохраняется — принтер
        /// различает их по имени, но для человека порядок в журнале важен.
        ///
        /// useCache=true применим только если формат уже один раз отправлялся принтеру
        /// вручную, иначе принтер вернёт ошибку — поэтому по умолчанию выключен.
        ///
        /// groupSeparatorAsEntity: значение может содержать разделитель групп 0x1D.
        /// Формально XML 1.0 не допускает такой символ в атрибуте, поэтому есть два
        /// режима: сырой байт (по умолчанию) и числовая сущность &amp;#x1D;.
        /// </summary>
        public static string SetPrintingFormat(string formatName,
                                               IEnumerable<KeyValuePair<string, string>> variables,
                                               bool useCache = false,
                                               bool groupSeparatorAsEntity = false)
        {
            var cache = useCache ? " useCache=\"True\"" : string.Empty;

            var sb = new StringBuilder();
            sb.Append("<PROTOCOL>").Append(Environment.NewLine);
            sb.Append("  <SET_PRINTING_FORMAT Format=\"").Append(Escape(formatName))
              .Append("\" Quantity=\"1\"").Append(cache).Append(">").Append(Environment.NewLine);

            if (variables != null)
            {
                foreach (var variable in variables)
                {
                    if (string.IsNullOrEmpty(variable.Key)) continue;

                    sb.Append("    <VARIABLE Name=\"").Append(Escape(variable.Key))
                      .Append("\" Value=\"").Append(EscapeValue(variable.Value, groupSeparatorAsEntity))
                      .Append("\" />").Append(Environment.NewLine);
                }
            }

            sb.Append("  </SET_PRINTING_FORMAT>").Append(Environment.NewLine);
            sb.Append("</PROTOCOL>");
            return sb.ToString();
        }

        /// <summary>
        /// Удобная обёртка для одного значения переменной.
        /// </summary>
        public static string SetPrintingFormat(string formatName, string variableName, string value,
                                               bool useCache = false, bool groupSeparatorAsEntity = false)
        {
            return SetPrintingFormat(
                formatName,
                new[] { new KeyValuePair<string, string>(variableName, value) },
                useCache,
                groupSeparatorAsEntity);
        }

        /// <summary>PRINT (Yes) или PAUSE (No).</summary>
        public static string SetPrintingStatus(bool printing)
        {
            return Wrap("<SET_PRINTING_STATUS Value=\"" + (printing ? "Yes" : "No") + "\" />");
        }

        /// <summary>Источник печати: внешний триггер, расстояние или таймер.</summary>
        public static string SetPrintConfigTrigger(string trigger)
        {
            return Wrap("<PRINT_CONFIG>" + Environment.NewLine
                        + "  <TRIGGER>" + Escape(trigger) + "</TRIGGER>" + Environment.NewLine
                        + "</PRINT_CONFIG>");
        }

        /// <summary>
        /// Предварительная загрузка списка значений переменной.
        /// По руководству принтер должен стоять на паузе, после загрузки
        /// подаётся SET_PRINTING_FORMAT.
        /// </summary>
        public static string SetVariableBatch(string variableName, IEnumerable<string> values)
        {
            var sb = new StringBuilder();
            sb.Append("<PROTOCOL>").Append(Environment.NewLine);
            if (values != null)
            {
                foreach (var value in values)
                {
                    sb.Append("  <SET_VARIABLE_BATCH Variable=\"" + Escape(variableName)
                             + "\" Values=\"" + EscapeValue(value, false) + "\" />")
                      .Append(Environment.NewLine);
                }
            }
            sb.Append("</PROTOCOL>");
            return sb.ToString();
        }

        public static string SetVariableBatchClear()
        {
            return Wrap("<SET_VARIABLE_BATCH_CLEAR />");
        }

        public static string GetVariableBatchCount()
        {
            return Wrap("<GET_VARIABLE_BATCH_COUNT />");
        }

        /// <summary>Удаление формата из памяти принтера.</summary>
        public static string DeleteFormat(string formatName)
        {
            return Wrap("<CMD_DELETE_FORMAT Name=\"" + Escape(formatName) + "\" />");
        }

        private static string Escape(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            return value
                .Replace("&", "&amp;")
                .Replace("<", "&lt;")
                .Replace(">", "&gt;")
                .Replace("\"", "&quot;")
                .Replace("'", "&apos;");
        }

        /// <summary>
        /// Экранирование значения переменной. Разделитель групп 0x1D либо остаётся
        /// сырым байтом, либо заменяется числовой сущностью.
        /// </summary>
        private static string EscapeValue(string value, bool groupSeparatorAsEntity)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;

            string escaped = Escape(value);

            if (groupSeparatorAsEntity)
            {
                escaped = escaped.Replace(Codes.CodeFactory.GroupSeparator.ToString(), "&#x1D;");
            }

            return escaped;
        }
    }
}
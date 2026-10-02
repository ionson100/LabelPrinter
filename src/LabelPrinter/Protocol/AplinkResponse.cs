using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace LabelPrinter.Protocol
{
    /// <summary>
    /// Разбор ответа принтера APLINK.
    ///
    /// Важно: ответы в протоколе приходят с НЕкавыми значениями атрибутов —
    ///     &lt;ANSWER Command=GET_COUNTER Value=OK&gt;
    /// а не &lt;ANSWER Command="GET_COUNTER" Value="OK"&gt;. Поэтому System.Xml
    /// здесь неприменим: разбор сделан регулярными выражениями.
    /// </summary>
    public sealed class AplinkResponse
    {
        public string Raw { get; set; }

        /// <summary>Имя команды из атрибута Command.</summary>
        public string Command { get; set; }

        /// <summary>Значение атрибута Value: OK | Yes | No | True | False.</summary>
        public string Value { get; set; }

        /// <summary>Текст ошибки, если принтер её вернул.</summary>
        public string Message { get; set; }

        public bool IsSuccess
        {
            get
            {
                if (string.IsNullOrEmpty(Value)) return false;
                var v = Value.Trim().ToLowerInvariant();
                return v == "ok" || v == "yes" || v == "true";
            }
        }

        // ---- GET_COUNTER -------------------------------------------------
        /// <summary>Общий счётчик принтера за всю жизнь.</summary>
        public long OverallCounter { get; private set; }

        /// <summary>Счётчик текущего задания — ради него и идёт печать.</summary>
        public long CustomCounter { get; private set; }

        // ---- GET_PRINTING_STATUS ----------------------------------------
        /// <summary>true — принтер готов печатать, false — на паузе.</summary>
        public bool? IsPrintReady { get; private set; }

        // ---- GET_FORMAT_LIST --------------------------------------------
        public List<string> Formats { get; private set; }

        // ---- QUEUE_STATUS ------------------------------------------------
        public bool? QueueEnabled { get; private set; }

        /// <summary>Сколько заданий в очереди (без текущего).</summary>
        public int? QueueItemCount { get; private set; }

        // ---- GET_ERROR_LIST ----------------------------------------------
        public List<string> Errors { get; private set; }

        // ---- GET_PRINT_CONFIG --------------------------------------------
        public string Speed { get; private set; }

        public string Trigger { get; private set; }

        // ---- GET_PRINTER_INFO ---------------------------------------------
        public string PrinterModel { get; private set; }

        public string SerialNumber { get; private set; }

        public string HwVersion { get; private set; }

        public string SwVersion { get; private set; }

        private static readonly Regex ReAnswer = new Regex(
            @"<ANSWER\s+Command\s*=\s*[""']?(?<cmd>[A-Za-z_0-9]+)[""']?\s+Value\s*=\s*[""']?(?<val>[^""'\s/>]+)[""']?",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex ReMessage = new Regex(
            @"Message\s*=\s*[""'](?<msg>[^""']*)[""']",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex ReOverall = new Regex(
            @"<COUNTER\s+Value\s*=\s*[""']?(?<n>-?\d+)[""']?", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex ReCustom = new Regex(
            @"<CUSTOM_COUNTER\s+Value\s*=\s*[""']?(?<n>-?\d+)[""']?", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex ReStatus = new Regex(
            @"<STATUS\s+Value\s*=\s*[""']?(?<v>True|False)[""']?", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex ReFormat = new Regex(
            @"<FORMAT\s+Value\s*=\s*[""']?(?<v>[^""'/>]*)[""']?\s*/?>", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex ReEnabled = new Regex(
            @"<ENABLED>\s*(?<v>\w+)\s*</ENABLED>", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex ReItemCount = new Regex(
            @"<ITEMCOUNT>\s*(?<v>\d+)\s*</ITEMCOUNT>", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex ReErrFlag = new Regex(
            @"<ERRFLAG\s+Name\s*=\s*[""']?(?<n>[A-Za-z_0-9]+)[""']?\s+Value\s*=\s*[""']?(?<v>True|False)[""']?",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex ReSpeed = new Regex(
            @"<SPEED\b[^>]*?Value\s*=\s*[""']?(?<v>[^""'\s/>]+)[""']?", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex ReTrigger = new Regex(
            @"<TRIGGER\b[^>]*?Value\s*=\s*[""']?(?<v>[^""'\s/>]+)[""']?", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex ReModel = new Regex(
            @"\bModel\s*=\s*[""']?(?<v>[^""'\s/>]+)[""']?", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex ReSerial = new Regex(
            @"\bSerial\s*=\s*[""']?(?<v>[^""'\s/>]+)[""']?", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex ReHw = new Regex(
            @"\bHW\s*=\s*[""']?(?<v>[^""'\s/>]+)[""']?", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex ReSw = new Regex(
            @"\bSW\s*=\s*[""']?(?<v>[^""'\s/>]+)[""']?", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex ReVariable = new Regex(
            @"<VARIABLE\s+Name\s*=\s*[""']?(?<n>[^""'\s/>]+)[""']?\s+Value\s*=\s*[""']?(?<v>[^""']*?)[""']?\s*/?>",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public static AplinkResponse Parse(string raw)
        {
            var response = new AplinkResponse
            {
                Raw = raw ?? string.Empty,
                Formats = new List<string>(),
                Errors = new List<string>()
            };

            var text = response.Raw;
            if (string.IsNullOrWhiteSpace(text)) return response;

            var answer = ReAnswer.Match(text);
            if (answer.Success)
            {
                response.Command = answer.Groups["cmd"].Value;
                response.Value = answer.Groups["val"].Value;
            }

            var message = ReMessage.Match(text);
            if (message.Success) response.Message = message.Groups["msg"].Value;

            var overall = ReOverall.Match(text);
            if (overall.Success)
            {
                long.TryParse(overall.Groups["n"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long value);
                response.OverallCounter = value;
            }

            var custom = ReCustom.Match(text);
            if (custom.Success)
            {
                long.TryParse(custom.Groups["n"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long value);
                response.CustomCounter = value;
            }

            var status = ReStatus.Match(text);
            if (status.Success)
            {
                response.IsPrintReady = status.Groups["v"].Value.Equals("True", StringComparison.OrdinalIgnoreCase);
            }

            foreach (Match m in ReFormat.Matches(text))
            {
                var value = m.Groups["v"].Value.Trim();
                if (value.Length > 0) response.Formats.Add(value);
            }

            var enabled = ReEnabled.Match(text);
            if (enabled.Success)
            {
                response.QueueEnabled = enabled.Groups["v"].Value.Equals("Yes", StringComparison.OrdinalIgnoreCase)
                                        || enabled.Groups["v"].Value.Equals("True", StringComparison.OrdinalIgnoreCase);
            }

            var items = ReItemCount.Match(text);
            if (items.Success)
            {
                int.TryParse(items.Groups["v"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value);
                response.QueueItemCount = value;
            }

            foreach (Match m in ReErrFlag.Matches(text))
            {
                if (m.Groups["v"].Value.Equals("True", StringComparison.OrdinalIgnoreCase))
                {
                    response.Errors.Add(m.Groups["n"].Value);
                }
            }

            var speed = ReSpeed.Match(text);
            if (speed.Success) response.Speed = speed.Groups["v"].Value;

            var trigger = ReTrigger.Match(text);
            if (trigger.Success) response.Trigger = trigger.Groups["v"].Value;

            var model = ReModel.Match(text);
            if (model.Success) response.PrinterModel = model.Groups["v"].Value;

            var serial = ReSerial.Match(text);
            if (serial.Success) response.SerialNumber = serial.Groups["v"].Value;

            var hw = ReHw.Match(text);
            if (hw.Success) response.HwVersion = hw.Groups["v"].Value;

            var sw = ReSw.Match(text);
            if (sw.Success) response.SwVersion = sw.Groups["v"].Value;

            return response;
        }

        /// <summary>Разбор ответа SET_PRINTING_FORMAT / GET_PRINTING_VARIABLES.</summary>
        public Dictionary<string, string> ParseVariables()
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match m in ReVariable.Matches(Raw ?? string.Empty))
            {
                result[m.Groups["n"].Value] = m.Groups["v"].Value;
            }
            return result;
        }

        /// <summary>Текст ошибки для журнала: сообщение принтера или сырой ответ.</summary>
        public string ErrorText()
        {
            if (!string.IsNullOrEmpty(Message)) return Message;
            if (string.IsNullOrWhiteSpace(Raw)) return "принтер не вернул данных";
            var flat = Raw.Replace("\r", " ").Replace("\n", " ").Trim();
            return flat.Length > 400 ? flat.Substring(0, 400) + "…" : flat;
        }

        public override string ToString()
        {
            return Command + " → " + Value + (string.IsNullOrEmpty(Message) ? "" : " (" + Message + ")");
        }
    }
}

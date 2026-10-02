using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LabelPrinter.Core;

namespace LabelPrinter.Protocol
{
    /// <summary>Клиент настоящего принтера APLINK (MRX72e / HRX) поверх TCP.</summary>
    public sealed class RealPrinterClient : IPrinterClient
    {
        private readonly PrinterSettings _settings;
        private readonly int _timeoutMs;
        private readonly bool _groupSeparatorAsEntity;
        private readonly AplinkClient _client;
        private PrinterLinkState _state = PrinterLinkState.Disconnected;

        public RealPrinterClient(PrinterSettings settings, int timeoutMs, bool groupSeparatorAsEntity = false)
        {
            _settings = settings ?? throw new ArgumentNullException("settings");
            _timeoutMs = timeoutMs;
            _groupSeparatorAsEntity = groupSeparatorAsEntity;
            _client = new AplinkClient();
        }

        public string Name { get { return _settings.Name; } }

        public bool IsConnected { get { return _state == PrinterLinkState.Connected && _client.IsConnected; } }

        public PrinterLinkState State { get { return _state; } }

        public async Task<bool> ConnectAsync(CancellationToken ct)
        {
            _state = PrinterLinkState.Connecting;
            try
            {
                await _client.ConnectAsync(_settings.Host, _settings.Port, _timeoutMs, ct).ConfigureAwait(false);
                _state = PrinterLinkState.Connected;
                return true;
            }
            catch
            {
                _state = PrinterLinkState.Disconnected;
                throw;
            }
        }

        public void Disconnect()
        {
            _client.Disconnect();
            _state = PrinterLinkState.Disconnected;
        }

        public void MarkLost()
        {
            _client.Disconnect();
            _state = PrinterLinkState.Reconnecting;
        }

        public async Task<long> GetPrintedCountAsync(CancellationToken ct)
        {
            var response = await SendAsync(Commands.GetCounter(), ct).ConfigureAwait(false);
            return response.CustomCounter;
        }

        public async Task<bool> GetPrintReadyAsync(CancellationToken ct)
        {
            var response = await SendAsync(Commands.GetPrintingStatus(), ct).ConfigureAwait(false);
            return response.IsPrintReady ?? false;
        }

        public Task SendLabelAsync(string formatName, string variableName, string value, CancellationToken ct)
        {
            return SendAsync(
                Commands.SetPrintingFormat(
                    formatName,
                    variableName,
                    value,
                    _settings.UseFormatCache,
                    _groupSeparatorAsEntity),
                ct);
        }

        public Task SetPrintStatusAsync(bool printing, CancellationToken ct)
        {
            return SendAsync(Commands.SetPrintingStatus(printing), ct);
        }

        public Task SetTriggerAsync(string trigger, CancellationToken ct)
        {
            return SendAsync(Commands.SetPrintConfigTrigger(trigger), ct);
        }

        public async Task<int> GetQueueItemCountAsync(CancellationToken ct)
        {
            var response = await SendAsync(Commands.QueueStatus(_settings.BoardId), ct).ConfigureAwait(false);
            return response.QueueItemCount ?? 0;
        }

        public Task ClearQueueAsync(CancellationToken ct)
        {
            return SendAsync(Commands.QueueClear(), ct);
        }

        public async Task<IReadOnlyList<string>> GetFormatListAsync(CancellationToken ct)
        {
            var response = await SendAsync(Commands.GetFormatList(), ct).ConfigureAwait(false);
            return response.Formats;
        }

        public async Task<IReadOnlyList<string>> GetActiveErrorsAsync(CancellationToken ct)
        {
            var response = await SendAsync(Commands.GetErrorList(), ct).ConfigureAwait(false);
            return response.Errors;
        }

        public async Task<string> DescribeAsync(CancellationToken ct)
        {
            var response = await SendAsync(Commands.GetPrinterInfo(), ct).ConfigureAwait(false);
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(response.PrinterModel)) parts.Add(response.PrinterModel);
            if (!string.IsNullOrEmpty(response.SerialNumber)) parts.Add("S/N " + response.SerialNumber);
            if (!string.IsNullOrEmpty(response.HwVersion)) parts.Add("HW " + response.HwVersion);
            if (!string.IsNullOrEmpty(response.SwVersion)) parts.Add("SW " + response.SwVersion);
            return parts.Count > 0 ? string.Join(", ", parts) : "модель не сообщена";
        }

        /// <summary>Форматы загружены в принтер — полезно при проверке настроек.</summary>
        public async Task<IReadOnlyList<string>> GetFormatsAsync(CancellationToken ct)
        {
            return await GetFormatListAsync(ct).ConfigureAwait(false);
        }

        private async Task<AplinkResponse> SendAsync(string commandXml, CancellationToken ct)
        {
            if (!_client.IsConnected)
            {
                throw new InvalidOperationException("Нет соединения с принтером " + _settings.Endpoint + ".");
            }

            var response = await _client.SendAsync(commandXml, _timeoutMs, ct).ConfigureAwait(false);

            if (!response.IsSuccess)
            {
                throw new PrinterCommandException(response.Command, response.ErrorText());
            }

            return response;
        }

        public void Dispose()
        {
            _client.Dispose();
        }
    }

    /// <summary>Принтер отклонил команду.</summary>
    public sealed class PrinterCommandException : Exception
    {
        public string Command { get; private set; }

        public PrinterCommandException(string command, string message)
            : base("Команда " + (command ?? "?") + " отклонена: " + message)
        {
            Command = command;
        }
    }

    /// <summary>
    /// Тексты команд XML-протокола APLINK.
    /// Порядок команд и их синтаксис — по «APLINK SERIES: XML PROTOCOL MANUAL 2.24».
    /// </summary>
    public static class Commands
    {
        private static string Wrap(string body)
        {
            return "<PROTOCOL>" + Environment.NewLine + body + Environment.NewLine + "</PROTOCOL>";
        }

        public static string GetCounter()
        {
            return Wrap("<GET_COUNTER />");
        }

        public static string GetPrintingStatus()
        {
            return Wrap("<GET_PRINTING_STATUS />");
        }

        public static string GetFormatList()
        {
            return Wrap("<GET_FORMAT_LIST />");
        }

        public static string GetErrorList()
        {
            return Wrap("<GET_ERROR_LIST />");
        }

        public static string GetPrinterInfo()
        {
            return Wrap("<GET_PRINTER_INFO />");
        }

        public static string GetPrintConfig()
        {
            return Wrap("<GET_PRINT_CONFIG />");
        }

        public static string GetVersions()
        {
            return Wrap("<GET_VERSIONS />");
        }

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
        /// значение в переменную и ставит задание в печать.
        ///
        /// useCache=true применим только если формат уже один раз отправлялся
        /// принтеру вручную, иначе принтер вернёт ошибку — поэтому по умолчанию выключен.
        ///
        /// groupSeparatorAsEntity: значение переменной может содержать разделитель
        /// групп 0x1D. Формально XML 1.0 не допускает такой символ в атрибуте,
        /// поэтому есть два режима: сырой байт (по умолчанию, так понимает
        /// принтер) и числовая сущность &amp;#x1D; для строгих парсеров.
        /// </summary>
        public static string SetPrintingFormat(string formatName, string variableName, string value, bool useCache = false, bool groupSeparatorAsEntity = false)
        {
            var cache = useCache ? " useCache=\"True\"" : string.Empty;

            return "<PROTOCOL>" + Environment.NewLine
                 + "  <SET_PRINTING_FORMAT Format=\"" + Escape(formatName) + "\" Quantity=\"1\"" + cache + ">" + Environment.NewLine
                 + "    <VARIABLE Name=\"" + Escape(variableName) + "\" Value=\"" + EscapeValue(value, groupSeparatorAsEntity) + "\" />" + Environment.NewLine
                 + "  </SET_PRINTING_FORMAT>" + Environment.NewLine
                 + "</PROTOCOL>";
        }

        public static string SetPrintingStatus(bool printing)
        {
            return Wrap("<SET_PRINTING_STATUS Value=\"" + (printing ? "Yes" : "No") + "\" />");
        }

        /// <summary>Выбор источника печати: внешний триггер, расстояние или таймер.</summary>
        public static string SetPrintConfigTrigger(string trigger)
        {
            return Wrap("<PRINT_CONFIG>" + Environment.NewLine
                        + "  <TRIGGER>" + Escape(trigger) + "</TRIGGER>" + Environment.NewLine
                        + "</PRINT_CONFIG>");
        }

        /// <summary>Предварительная загрузка списка значений переменной (SET_VARIABLE_BATCH).</summary>
        public static string SetVariableBatch(string variableName, IEnumerable<string> values)
        {
            var sb = new StringBuilder();
            sb.Append("<PROTOCOL>").Append(Environment.NewLine);
            foreach (var value in values)
            {
                sb.Append("  <SET_VARIABLE_BATCH Variable=\"" + Escape(variableName) + "\" Values=\"" + Escape(value) + "\" />")
                  .Append(Environment.NewLine);
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
        /// сырым байтом, либо заменяется числовой сущностью — что именно понимает
        /// принтер, проверяется на стенде, поэтому оба варианта доступны.
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

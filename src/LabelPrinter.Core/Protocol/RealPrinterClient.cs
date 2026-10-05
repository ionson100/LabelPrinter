using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using LabelPrinter.Core.Configuration;
using LabelPrinter.Core.Diagnostics;

namespace LabelPrinter.Core.Protocol
{
    /// <summary>Клиент настоящего принтера APLINK (MRX72e / HRX) поверх TCP.</summary>
    public sealed class RealPrinterClient : IPrinterClient
    {
        private readonly PrinterSettings _settings;
        private readonly int _timeoutMs;
        private readonly bool _groupSeparatorAsEntity;
        private readonly AplinkClient _client;
        private PrinterLinkState _state = PrinterLinkState.Disconnected;

        /// <summary>Клиент настоящего принтера APLINK (MRX72e / HRX) поверх TCP.</summary>
        /// <param name="settings">Настройки принтера: адрес, порт, формат, переменная.</param>
        /// <param name="timeoutMs">Таймаут обмена, мс.</param>
        /// <param name="groupSeparatorAsEntity">Передавать 0x1D как &amp;#x1D;.</param>
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

        public async Task<bool> ConnectAsync(CancellationToken cancellationToken)
        {
            _state = PrinterLinkState.Connecting;
            try
            {
                await _client.ConnectAsync(_settings.Host, _settings.Port, _timeoutMs, cancellationToken)
                           .ConfigureAwait(false);
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

        /// <summary>Связь потеряна, но переподключение ещё не начато.</summary>
        public void MarkLost()
        {
            _client.Disconnect();
            _state = PrinterLinkState.Reconnecting;
        }

        public async Task<long> GetPrintedCountAsync(CancellationToken cancellationToken)
        {
            var response = await SendAsync(Commands.GetCounter(), cancellationToken).ConfigureAwait(false);
            return response.CustomCounter;
        }

        public async Task<bool> GetPrintReadyAsync(CancellationToken cancellationToken)
        {
            var response = await SendAsync(Commands.GetPrintingStatus(), cancellationToken).ConfigureAwait(false);
            return response.IsPrintReady ?? false;
        }

        public Task SendLabelAsync(string formatName, string variableName, string value, CancellationToken cancellationToken)
        {
            return SendAsync(
                Commands.SetPrintingFormat(formatName, variableName, value, _settings.UseFormatCache, _groupSeparatorAsEntity),
                cancellationToken);
        }

        public Task SetPrintStatusAsync(bool printing, CancellationToken cancellationToken)
        {
            return SendAsync(Commands.SetPrintingStatus(printing), cancellationToken);
        }

        public Task SetTriggerAsync(string trigger, CancellationToken cancellationToken)
        {
            return SendAsync(Commands.SetPrintConfigTrigger(trigger), cancellationToken);
        }

        public async Task<int> GetQueueItemCountAsync(CancellationToken cancellationToken)
        {
            var response = await SendAsync(Commands.QueueStatus(_settings.BoardId), cancellationToken).ConfigureAwait(false);
            return response.QueueItemCount ?? 0;
        }

        public Task ClearQueueAsync(CancellationToken cancellationToken)
        {
            return SendAsync(Commands.QueueClear(), cancellationToken);
        }

        public async Task<IReadOnlyList<string>> GetFormatListAsync(CancellationToken cancellationToken)
        {
            var response = await SendAsync(Commands.GetFormatList(), cancellationToken).ConfigureAwait(false);
            return response.Formats;
        }

        public async Task<IReadOnlyList<string>> GetActiveErrorsAsync(CancellationToken cancellationToken)
        {
            var response = await SendAsync(Commands.GetErrorList(), cancellationToken).ConfigureAwait(false);
            return response.Errors;
        }

        public async Task<string> DescribeAsync(CancellationToken cancellationToken)
        {
            var response = await SendAsync(Commands.GetPrinterInfo(), cancellationToken).ConfigureAwait(false);
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(response.PrinterModel)) parts.Add(response.PrinterModel);
            if (!string.IsNullOrEmpty(response.SerialNumber)) parts.Add("S/N " + response.SerialNumber);
            if (!string.IsNullOrEmpty(response.HwVersion)) parts.Add("HW " + response.HwVersion);
            if (!string.IsNullOrEmpty(response.SwVersion)) parts.Add("SW " + response.SwVersion);
            return parts.Count > 0 ? string.Join(", ", parts) : "модель не сообщена";
        }

        private async Task<AplinkResponse> SendAsync(string commandXml, CancellationToken cancellationToken)
        {
            if (!_client.IsConnected)
            {
                throw new InvalidOperationException("Нет соединения с принтером " + _settings.Endpoint + ".");
            }

            var response = await _client.SendAsync(commandXml, _timeoutMs, cancellationToken).ConfigureAwait(false);

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
}
using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LabelPrinter.Core;

namespace LabelPrinter.Protocol
{
    /// <summary>
    /// Транспорт протокола APLINK (XML Protocol Manual 2.24).
    ///
    /// Каждое сообщение обрамляется служебными байтами:
    ///     начало: 0x27 0x01 0x27 0x02
    ///     конец : 0x27 0x03 0x27 0x04
    ///
    /// Важно: по протоколу за раз разрешено ОДНО сообщение, поэтому запросы
    /// выстроены в очередь семафором. Таймаут задаётся на каждую операцию.
    /// </summary>
    public sealed class AplinkClient : IDisposable
    {
        public static readonly byte[] StartHeader = { 0x27, 0x01, 0x27, 0x02 };
        public static readonly byte[] EndHeader = { 0x27, 0x03, 0x27, 0x04 };

        private readonly SemaphoreSlim _requestLock = new SemaphoreSlim(1, 1);
        private readonly object _stateGate = new object();

        private TcpClient _client;
        private NetworkStream _stream;
        private string _host;
        private int _port;

        public bool IsConnected
        {
            get
            {
                lock (_stateGate)
                {
                    return _client != null && _client.Connected;
                }
            }
        }

        public string Endpoint
        {
            get { return (_host ?? "?") + ":" + _port; }
        }

        // ------------------------------------------------------------------
        //  Подключение
        // ------------------------------------------------------------------

        public async Task<bool> ConnectAsync(string host, int port, int timeoutMs, CancellationToken ct)
        {
            Disconnect();

            var client = new TcpClient();
            client.NoDelay = true;

            using (var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                connectCts.CancelAfter(timeoutMs);
                try
                {
                    await client.ConnectAsync(host, port).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    SafeClose(client);
                    throw new TimeoutException("Таймаут подключения к " + host + ":" + port + " (" + timeoutMs + " мс).");
                }
                catch
                {
                    SafeClose(client);
                    throw;
                }
            }

            var stream = client.GetStream();
            stream.ReadTimeout = timeoutMs;
            stream.WriteTimeout = timeoutMs;

            lock (_stateGate)
            {
                _client = client;
                _stream = stream;
                _host = host;
                _port = port;
            }

            Log.Success("Соединение с принтером " + host + ":" + port + " установлено.");
            return true;
        }

        public void Disconnect()
        {
            TcpClient old;
            NetworkStream oldStream;

            lock (_stateGate)
            {
                old = _client;
                oldStream = _stream;
                _client = null;
                _stream = null;
            }

            SafeClose(old);
            try { if (oldStream != null) oldStream.Dispose(); } catch { /* уже закрыт */ }
        }

        private static void SafeClose(TcpClient client)
        {
            if (client == null) return;
            try { client.Close(); } catch { /* уже закрыт */ }
            try { client.Dispose(); } catch { /* уже закрыт */ }
        }

        // ------------------------------------------------------------------
        //  Обмен
        // ------------------------------------------------------------------

        /// <summary>
        /// Отправляет XML-команду и читает ответ. Возвращает разобранный ответ.
        /// Бросает исключение, если связи нет или принтер не ответил вовремя.
        /// </summary>
        public async Task<AplinkResponse> SendAsync(string xml, int timeoutMs, CancellationToken ct)
        {
            await _requestLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                NetworkStream stream;
                lock (_stateGate)
                {
                    stream = _stream;
                    if (stream == null || _client == null || !_client.Connected)
                    {
                        throw new InvalidOperationException("Нет соединения с принтером " + Endpoint + ".");
                    }
                }

                var payload = BuildFrame(xml);

                using (var writeCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    writeCts.CancelAfter(timeoutMs);
                    await stream.WriteAsync(payload, 0, payload.Length, writeCts.Token).ConfigureAwait(false);
                    await stream.FlushAsync(writeCts.Token).ConfigureAwait(false);
                }

                var raw = await ReadResponseAsync(stream, timeoutMs, ct).ConfigureAwait(false);
                return AplinkResponse.Parse(raw);
            }
            catch (IOException ex)
            {
                Disconnect();
                throw new IOException("Связь с принтером " + Endpoint + " потеряна: " + ex.Message, ex);
            }
            catch (SocketException ex)
            {
                Disconnect();
                throw new IOException("Сетевая ошибка принтера " + Endpoint + ": " + ex.SocketErrorCode, ex);
            }
            finally
            {
                _requestLock.Release();
            }
        }

        /// <summary>Оборачивает команду в PROTOCOL и служебные байты.</summary>
        public static byte[] BuildFrame(string commandXml)
        {
            var body = Encoding.UTF8.GetBytes(commandXml ?? string.Empty);

            var buffer = new byte[StartHeader.Length + body.Length + EndHeader.Length];
            Buffer.BlockCopy(StartHeader, 0, buffer, 0, StartHeader.Length);
            Buffer.BlockCopy(body, 0, buffer, StartHeader.Length, body.Length);
            Buffer.BlockCopy(EndHeader, 0, buffer, StartHeader.Length + body.Length, EndHeader.Length);
            return buffer;
        }

        /// <summary>
        /// Читает ответ до концевого маркера 0x27 0x03 0x27 0x04.
        ///
        /// Сделано с допуском: некоторые прошивки не присылают маркеры, поэтому если
        /// начальный маркер так и не встретился, ответ вычитывается по закрывающему
        /// тегу &lt;/PROTOCOL&gt;.
        /// </summary>
        private static async Task<string> ReadResponseAsync(NetworkStream stream, int timeoutMs, CancellationToken ct)
        {
            var accumulator = new MemoryStream();
            var chunk = new byte[4096];
            bool sawStartHeader = false;

            using (var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                timeoutCts.CancelAfter(timeoutMs);

                try
                {
                    while (true)
                    {
                        int read;
                        try
                        {
                            read = await stream.ReadAsync(chunk, 0, chunk.Length, timeoutCts.Token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                        {
                            throw new TimeoutException("Принтер не ответил за " + timeoutMs + " мс.");
                        }

                        if (read <= 0)
                        {
                            throw new IOException("Принтер закрыл соединение.");
                        }

                        accumulator.Write(chunk, 0, read);

                        byte[] buffer = accumulator.ToArray();

                        if (!sawStartHeader && buffer.Length >= 4 && StartsWith(buffer, StartHeader))
                        {
                            sawStartHeader = true;
                        }

                        int end = IndexOf(buffer, EndHeader);
                        if (end >= 0)
                        {
                            var text = TrimFrame(Encoding.UTF8.GetString(buffer, 0, end), sawStartHeader);
                            if (text != null) return text;
                        }

                        // Запасной путь: маркеров нет, но тег закрыт.
                        if (!sawStartHeader)
                        {
                            string current = Encoding.UTF8.GetString(buffer);
                            int close = current.LastIndexOf("</PROTOCOL>", StringComparison.OrdinalIgnoreCase);
                            if (close >= 0)
                            {
                                return current.Substring(0, close + "</PROTOCOL>".Length);
                            }
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                finally
                {
                    accumulator.Dispose();
                }
            }
        }

        private static string TrimFrame(string text, bool hadStartHeader)
        {
            if (text == null) return null;
            var trimmed = text.Trim();
            if (trimmed.Length == 0) return null;

            // Отрезаем начальный маркер, если он попал в строку.
            if (hadStartHeader)
            {
                int index = trimmed.IndexOf("<PROTOCOL", StringComparison.OrdinalIgnoreCase);
                if (index > 0) trimmed = trimmed.Substring(index);
            }
            return trimmed.Length == 0 ? null : trimmed;
        }

        private static bool StartsWith(byte[] data, byte[] prefix)
        {
            if (data.Length < prefix.Length) return false;
            for (int i = 0; i < prefix.Length; i++)
            {
                if (data[i] != prefix[i]) return false;
            }
            return true;
        }

        private static int IndexOf(byte[] data, byte[] pattern)
        {
            for (int i = 0; i <= data.Length - pattern.Length; i++)
            {
                bool match = true;
                for (int j = 0; j < pattern.Length; j++)
                {
                    if (data[i + j] != pattern[j]) { match = false; break; }
                }
                if (match) return i;
            }
            return -1;
        }

        public void Dispose()
        {
            Disconnect();
            _requestLock.Dispose();
        }
    }
}

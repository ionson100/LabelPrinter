using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using LabelPrinter.Core.Abstractions;
using LabelPrinter.Core.Codes;
using LabelPrinter.Core.Configuration;
using LabelPrinter.Core.Diagnostics;
using LabelPrinter.Core.Runtime;

namespace LabelPrinter.Core.Sample
{
    /// <summary>
    /// Пример подключения LabelPrinter.Core к постороннему приложению.
    ///
    /// Здесь нет ни WPF, ни PostgreSQL, ни Redis — только библиотека и
    /// собственная реализация ICodeSource. Если этот проект собирается и печатает,
    /// значит библиотеку действительно можно положить рядом с любым приложением.
    ///
    /// Запуск:
    ///     LabelPrinter.Core.Sample.exe 192.168.1.10 4100 demo code
    /// </summary>
    internal static class Program
    {
        private static async Task<int> Main(string[] args)
        {
            string host = args.Length > 0 ? args[0] : "192.168.1.10";
            int port = args.Length > 1 ? int.Parse(args[1]) : 4100;
            string format = args.Length > 2 ? args[2] : "demo";
            string variable = args.Length > 3 ? args[3] : "code";

            Console.OutputEncoding = System.Text.Encoding.UTF8;

            // Журнал библиотеки выводим в консоль. По умолчанию он молчит —
            // чтобы библиотека не печатала в Console там, где это не нужно.
            Log.Sink = new DelegateLogSink(m => Console.WriteLine(m.Line));

            var codeFormat = new CodeFormatSettings();
            Console.WriteLine("Формат кода: " + CodeFactory.ToHumanReadable(CodeFactory.Build(codeFormat)));
            Console.WriteLine();

            var runtimeSettings = new PrintRuntimeSettings
            {
                PollIntervalMs = 100,
                BufferSize = 100,
                BufferRefillThreshold = 10,
                TestPrinterMode = false
            };

            using (var engine = new PrintEngine(runtimeSettings, codeFormat))
            {
                // 1. Откуда берём коды — единственное, что нужно решить приложению.
                engine.CodeSource = new SequenceCodeSource();

                // 2. Какие принтеры обслуживать.
                engine.Printers.Add(new PrinterSettings("Принтер 1", host, port)
                {
                    FormatName = format,
                    VariableName = variable,
                    Enabled = true
                });

                var cts = new CancellationTokenSource();

                // 3. Подключение.
                Console.WriteLine("Подключение к " + host + ":" + port + "…");
                await engine.StartAsync(cts.Token);

                await WaitForConnectionAsync(engine, cts.Token);

                if (engine.Runtimes.Count == 0 || !engine.Runtimes[0].IsConnected)
                {
                    Console.Error.WriteLine("Принтер не подключился. Проверьте адрес и порт.");
                    return 1;
                }

                // 4. Печать.
                Console.WriteLine();
                Console.WriteLine("Запуск печати. Остановка по Ctrl+C.");
                await engine.StartPrintingAsync(cts.Token);

                Console.CancelKeyPress += (s, e) =>
                {
                    e.Cancel = true;
                    cts.Cancel();
                };

                try
                {
                    await Task.Delay(Timeout.Infinite, cts.Token);
                }
                catch (OperationCanceledException)
                {
                }

                // 5. Остановка.
                await engine.StopPrintingAsync(CancellationToken.None);

                var printed = engine.Runtimes.Count > 0 ? engine.Runtimes[0].PrintedThisSession : 0;
                Console.WriteLine();
                Console.WriteLine("Напечатано за сеанс: " + printed);
            }

            return 0;
        }

        private static async Task WaitForConnectionAsync(PrintEngine engine, CancellationToken token)
        {
            // Подключение идёт в фоновом цикле, поэтому ждём, пока связь появится.
            // Состояние печатаем только при смене — иначе экран засоряется одинаковыми строками.
            string lastState = null;

            for (int i = 0; i < 60 && !token.IsCancellationRequested; i++)
            {
                var runtimes = engine.Runtimes;
                if (runtimes.Count > 0)
                {
                    if (runtimes[0].IsConnected) return;

                    var state = runtimes[0].LinkText;
                    if (state != lastState)
                    {
                        Console.WriteLine("  " + state);
                        lastState = state;
                    }
                }
                await Task.Delay(500, token).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Источник кодов для примера: простой счётчик.
    ///
    /// В реальном приложении здесь будет ваша база, файл или очередь сообщений.
    /// Важно только то, что движок просит долить буфер и забрать один код.
    /// </summary>
    internal sealed class SequenceCodeSource : ICodeSource
    {
        private readonly Queue<string> _buffer = new Queue<string>();
        private readonly CodeFormatSettings _format = new CodeFormatSettings();
        private long _produced;
        private long _limit = 1000;

        public Task<int> GetBufferCountAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(_buffer.Count);
        }

        public Task<int> EnsureBufferAsync(int capacity, int threshold, CancellationToken cancellationToken)
        {
            if (_buffer.Count > threshold) return Task.FromResult(0);

            int added = 0;
            while (_buffer.Count < capacity && _produced < _limit)
            {
                _buffer.Enqueue(CodeFactory.Build(_format));
                _produced++;
                added++;
            }
            return Task.FromResult(added);
        }

        public Task<string> TakeAsync(CancellationToken cancellationToken)
        {
            if (_buffer.Count == 0) EnsureBufferAsync(100, 0, cancellationToken).GetAwaiter().GetResult();
            return Task.FromResult(_buffer.Count > 0 ? _buffer.Dequeue() : null);
        }

        public Task<long> GetRemainingAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(Math.Max(0, _limit - _produced));
        }

        public Task ClearAsync(CancellationToken cancellationToken)
        {
            _buffer.Clear();
            return Task.CompletedTask;
        }
    }
}
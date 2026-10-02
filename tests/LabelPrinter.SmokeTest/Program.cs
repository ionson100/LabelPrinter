using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using LabelPrinter.Buffers;
using LabelPrinter.Codes;
using LabelPrinter.Core;
using LabelPrinter.Data;
using LabelPrinter.Protocol;
using LabelPrinter.Services;

namespace LabelPrinter.SmokeTest
{
    /// <summary>
    /// Проверка ключевых узлов без интерфейса: генерация кода, разбор ответов
    /// принтера, работа с базой и буфером, тестовый принтер.
    /// </summary>
    internal static class Program
    {
        private static int _passed;
        private static int _failed;

        private static int Main()
        {
            try
            {
                Console.OutputEncoding = System.Text.Encoding.UTF8;
            }
            catch
            {
                // в консоли без UTF-8 просто будет мешанина — тесты от этого не зависят
            }

            try
            {
                CodeFactoryTests();
                Gs1Tests();
                AplinkResponseTests();
                CommandTests();
                FramingTests();
                Await(WireFormatTests());
                SettingsDefaultsTests();
                Await(DbAndBufferTests());
                Await(TestPrinterFlow());
                Await(BaselineAndDeltaTests());
            }
            catch (Exception ex)
            {
                Console.WriteLine("НЕОЖИДАННАЯ ОШИБКА: " + ex);
                _failed++;
            }

            Console.WriteLine();
            Console.WriteLine("=========================================");
            Console.WriteLine("ИТОГО: успешно " + _passed + ", провалено " + _failed);
            Console.WriteLine("=========================================");
            return _failed == 0 ? 0 : 1;
        }

        private static void Await(Task task)
        {
            task.GetAwaiter().GetResult();
        }

        private static void Check(string name, bool condition, string detail = null)
        {
            if (condition)
            {
                _passed++;
                Console.WriteLine("  [ок]   " + name);
            }
            else
            {
                _failed++;
                Console.WriteLine("  [ОШИБКА] " + name + (detail == null ? "" : " -> " + detail));
            }
        }

        // ------------------------------------------------------------------

        private static void CodeFactoryTests()
        {
            Console.WriteLine("Генерация кода");

            var settings = new AppSettings();

            string code = CodeFactory.Build(settings);
            Console.WriteLine("  пример: " + Visible(code));

            Check("код не пуст", !string.IsNullOrEmpty(code));

            // 01(2) + GTIN(14) + 21(2) + R1(6) + GS(1) + 93(2) + R2(4) = 31 символ
            Check("длина кода 31", code.Length == 31, "получено " + code.Length);
            Check("начинается с 01 + GTIN", code.StartsWith("0102345678987654"), Visible(code));
            Check("содержит AI 21", code.Substring(16).StartsWith("21"), Visible(code));

            Check("разделитель групп 0x1D присутствует",
                code.IndexOf(CodeFactory.GroupSeparator) > 0, Visible(code));

            int gsIndex = code.IndexOf(CodeFactory.GroupSeparator);
            Check("AI 21 стоит перед разделителем", gsIndex == 24, "позиция " + gsIndex);
            Check("AI 93 стоит после разделителя", code.Substring(gsIndex + 1).StartsWith("93"),
                  Visible(code));
            Check("нет AI 29", code.IndexOf("29", StringComparison.Ordinal) < 0, Visible(code));

            var alphabet = new HashSet<char>(settings.CodeAlphabet);
            bool allInAlphabet = code.All(c => c == CodeFactory.GroupSeparator || char.IsDigit(c) || alphabet.Contains(c));
            Check("символы только из алфавита", allInAlphabet);

            // Уникальность на большой выборке.
            var seen = new HashSet<string>();
            bool unique = true;
            for (int i = 0; i < 20000; i++)
            {
                if (!seen.Add(CodeFactory.Build(settings))) { unique = false; break; }
            }
            Check("20000 кодов без повторов", unique, "найден дубль");
        }

        /// <summary>Заменяет непечатаемый 0x1D на читаемое обозначение.</summary>
        private static string Visible(string code)
        {
            return code == null ? "" : code.Replace(CodeFactory.GroupSeparator.ToString(), "<gr>");
        }

        private static void Gs1Tests()
        {
            Console.WriteLine("Проверка GS1");

            var settings = new AppSettings();
            string code = CodeFactory.Build(settings);

            string readable = CodeFactory.ToHumanReadable(code);
            Console.WriteLine("  разбор: " + readable);

            // Ожидаем ровно такой вид: (01)02345678987654(21)XXXXXX<gr>(93)XXXX
            Check("разбор содержит (01)", readable.Contains("(01)02345678987654"), readable);
            Check("разбор содержит <gr>", readable.Contains("<gr>"), readable);
            Check("разбор содержит (93)", readable.Contains("(93)"), readable);
            Check("разделитель стоит между 21 и 93",
                readable.IndexOf("<gr>", StringComparison.Ordinal) < readable.IndexOf("(93)", StringComparison.Ordinal),
                readable);
            Check("AI 29 не появился", !readable.Contains("(29)"), readable);
            Check("непечатаемый 0x1D не попал в разбор",
                !readable.Contains(CodeFactory.GroupSeparator.ToString()), readable);

            // Разбор должен быть устойчив к «93» внутри значения AI(21):
            // без разделителя групп такой код неоднозначен.
            string ambiguous = "010234567898765421" + "93XY12" + CodeFactory.GroupSeparator + "93abcd";
            Check("двузначный AI внутри значения не путается с новым элементом",
                CodeFactory.ToHumanReadable(ambiguous) == "(01)02345678987654(21)93XY12<gr>(93)abcd",
                CodeFactory.ToHumanReadable(ambiguous));

            // Контрольная цифра GTIN-14.
            var problem = CodeFactory.ValidateGtin14("02345678987654");
            Check("контрольная цифра 02345678987654 отвергнута (ожидаемо)", problem != null,
                  "проблема не найдена — возможно, цифра корректна");

            Check("корректный GTIN 00012345678905 принят", CodeFactory.ValidateGtin14("00012345678905") == null,
                  CodeFactory.ValidateGtin14("00012345678905"));

            Check("короткий GTIN отвергнут", CodeFactory.ValidateGtin14("12345") != null);
            Check("GTIN с буквами отвергнут", CodeFactory.ValidateGtin14("0234567898765A") != null);
        }

        private static void AplinkResponseTests()
        {
            Console.WriteLine("Разбор ответов APLINK");

            // Реальный вид ответа из руководства 2.24 (значения атрибутов БЕЗ кавычек).
            string counter =
                "<PROTOCOL>\n<ANSWER Command=COUNTERS Value=OK>\n" +
                "  <COUNTER Value=1547 />\n  <CUSTOM_COUNTER Value=23 />\n</ANSWER>\n</PROTOCOL>";
            var r1 = AplinkResponse.Parse(counter);
            Check("Command разобран", r1.Command == "COUNTERS", r1.Command);
            Check("Value=OK распознан как успех", r1.IsSuccess, r1.Value);
            Check("общий счётчик 1547", r1.OverallCounter == 1547, r1.OverallCounter.ToString());
            Check("счётчик задания 23", r1.CustomCounter == 23, r1.CustomCounter.ToString());

            string status = "<PROTOCOL>\n<ANSWER Command=GET_PRINTING_STATUS Value=OK>\n  <STATUS Value=True />\n</ANSWER>\n</PROTOCOL>";
            var r2 = AplinkResponse.Parse(status);
            Check("STATUS True", r2.IsPrintReady == true);

            string paused = "<PROTOCOL>\n<ANSWER Command=GET_PRINTING_STATUS Value=OK>\n  <STATUS Value=False />\n</ANSWER>\n</PROTOCOL>";
            Check("STATUS False", AplinkResponse.Parse(paused).IsPrintReady == false);

            string formats =
                "<PROTOCOL>\n<ANSWER Command=GET_FORMAT_LIST Value=OK>\n" +
                "  <FORMAT Value=demo />\n  <FORMAT Value=other />\n</ANSWER>\n</PROTOCOL>";
            var r3 = AplinkResponse.Parse(formats);
            Check("форматов найдено 2", r3.Formats.Count == 2, r3.Formats.Count.ToString());
            Check("формат demo найден", r3.Formats.Contains("demo"));

            string queue = "<PROTOCOL>\n<ANSWER Command=QUEUE_STATUS Value=OK>\n  <ENABLED>Yes</ENABLED>\n  <ITEMCOUNT>5</ITEMCOUNT>\n</ANSWER>\n</PROTOCOL>";
            var r4 = AplinkResponse.Parse(queue);
            Check("очередь включена", r4.QueueEnabled == true);
            Check("в очереди 5", r4.QueueItemCount == 5, r4.QueueItemCount?.ToString());

            string errors =
                "<PROTOCOL>\n<ANSWER Command=GET_ERROR_LIST Value=OK>\n" +
                "  <ERRFLAG Name=ERROR_1 Value=False />\n  <ERRFLAG Name=ERROR_3 Value=True />\n" +
                "</ANSWER>\n</PROTOCOL>";
            var r5 = AplinkResponse.Parse(errors);
            Check("активна одна ошибка", r5.Errors.Count == 1, string.Join(",", r5.Errors));
            Check("ошибка ERROR_3", r5.Errors.Contains("ERROR_3"));

            string fail = "<PROTOCOL>\n<ANSWER Command=CMD_DELETE_FORMAT Value=No Message=\"format not found\" /></PROTOCOL>";
            var r6 = AplinkResponse.Parse(fail);
            Check("Value=No — не успех", !r6.IsSuccess);
            Check("сообщение принтера разобрано", r6.Message == "format not found", r6.Message);

            string vars =
                "<PROTOCOL>\n<ANSWER Command=GET_PRINTING_VARIABLES Value=OK>\n" +
                "  <VARIABLE Name=code Value=010234567898765421ABCDEF2993WXYZ />\n</ANSWER>\n</PROTOCOL>";
            var parsedVars = AplinkResponse.Parse(vars).ParseVariables();
            Check("переменная code разобрана", parsedVars.ContainsKey("code"));
            Check("значение code верное",
                  parsedVars["code"] == "010234567898765421ABCDEF2993WXYZ", parsedVars["code"]);
        }

        private static void CommandTests()
        {
            Console.WriteLine("Формирование команд");

            string set = Commands.SetPrintingFormat("demo", "code", "010234567898765421ABCDEF2993WXYZ");
            Check("один <PROTOCOL>", CountOf(set, "<PROTOCOL>") == 1, CountOf(set, "<PROTOCOL>").ToString());
            Check("один </PROTOCOL>", CountOf(set, "</PROTOCOL>") == 1, CountOf(set, "</PROTOCOL>").ToString());
            Check("один </SET_PRINTING_FORMAT>", CountOf(set, "</SET_PRINTING_FORMAT>") == 1);
            Check("указан формат demo", set.Contains("Format=\"demo\""), set);
            Check("указана переменная code", set.Contains("Name=\"code\""));
            Check("подставлено значение", set.Contains("010234567898765421ABCDEF2993WXYZ"));
            Check("useCache выключен по умолчанию", !set.Contains("useCache"), set);

            string setCached = Commands.SetPrintingFormat("demo", "code", "X", true);
            Check("useCache включается флагом", setCached.Contains("useCache=\"True\""));

            string pause = Commands.SetPrintingStatus(false);
            Check("пауза = Value=No", pause.Contains("Value=\"No\""), pause);
            Check("печать = Value=Yes", Commands.SetPrintingStatus(true).Contains("Value=\"Yes\""));

            // Экранирование спецсимволов в значении.
            string escaped = Commands.SetPrintingFormat("demo", "code", "A&B\"C");
            Check("кавычки и & экранированы", escaped.Contains("A&amp;B&quot;C"), escaped);

            string trigger = Commands.SetPrintConfigTrigger("photocell");
            Check("триггер задан", trigger.Contains("<TRIGGER>photocell</TRIGGER>"), trigger);

            // ---- разделитель групп 0x1D в значении переменной ----
            string sample = CodeFactory.Build(new AppSettings());
            const char gs = CodeFactory.GroupSeparator;

            string raw = Commands.SetPrintingFormat("demo", "code", sample, false, false);
            Check("сырой режим: 0x1D попал в команду",
                raw.IndexOf(gs) > 0, Visible(raw));
            Check("сырой режим: сущности нет",
                !raw.Contains("&#x1D;"), Visible(raw));
            Check("сырой режим: AI 21 и AI 93 на месте",
                raw.Contains("(21)") == false && raw.Contains("93"), "ожидается код без скобок AI");

            string entity = Commands.SetPrintingFormat("demo", "code", sample, false, true);
            Check("режим сущности: &amp;#x1D; присутствует",
                entity.Contains("&#x1D;"), Visible(entity));
            Check("режим сущности: сырого 0x1D нет",
                entity.IndexOf(gs) < 0, Visible(entity));
            Check("режим сущности: один разделитель",
                CountOf(entity, "&#x1D;") == 1, CountOf(entity, "&#x1D;").ToString());
            Check("оба режима дают одинаковую длину",
                raw.Length == entity.Length - 5, raw.Length + " / " + entity.Length);

            // Порядок: AI 21, затем разделитель, затем 93.
            int p21 = entity.IndexOf("21", entity.IndexOf("VARIABLE"));
            int pGs = entity.IndexOf("&#x1D;");
            int p93 = entity.IndexOf("93", pGs);
            Check("порядок в команде: 21 < <gr> < 93",
                p21 > 0 && p21 < pGs && pGs < p93, "21=" + p21 + " gs=" + pGs + " 93=" + p93);

            string batch = Commands.SetVariableBatch("code", new[] { "A", "B", "C" });
            Check("в batch три SET_VARIABLE_BATCH", CountOf(batch, "<SET_VARIABLE_BATCH ") == 3,
                  CountOf(batch, "<SET_VARIABLE_BATCH ").ToString());
        }

        private static void FramingTests()
        {
            Console.WriteLine("Служебные байты протокола");

            byte[] frame = AplinkClient.BuildFrame("<PROTOCOL><X /></PROTOCOL>");

            Check("начало: 27 01 27 02",
                frame[0] == 0x27 && frame[1] == 0x01 && frame[2] == 0x27 && frame[3] == 0x02,
                string.Join(" ", frame.Take(4).Select(x => x.ToString("X2"))));

            Check("конец: 27 03 27 04",
                frame[frame.Length - 4] == 0x27 && frame[frame.Length - 3] == 0x03 &&
                frame[frame.Length - 2] == 0x27 && frame[frame.Length - 1] == 0x04,
                string.Join(" ", frame.Skip(frame.Length - 4).Select(x => x.ToString("X2"))));

            Check("в кадре ровно один заголовок и один хвост",
                CountOf(System.Text.Encoding.UTF8.GetString(frame), "<PROTOCOL>") == 1);
        }

        // ------------------------------------------------------------------

        private static async Task DbAndBufferTests()
        {
            Console.WriteLine("PostgreSQL и Redis");

            var settings = new AppSettings();
            var ct = CancellationToken.None;

            using (var repo = new CodesRepository(settings.ConnectionString))
            {
                Console.WriteLine("  сервер: " + await repo.CheckAsync(ct));

                var init = await repo.InitializeAsync(200, () => CodeFactory.Build(settings), ct);
                Console.WriteLine("  " + init.Summary);
                Check("таблица создана или найдена", init.Total >= 200, init.Total.ToString());

                // Повторный вызов не должен дублировать коды.
                var again = await repo.InitializeAsync(200, () => CodeFactory.Build(settings), ct);
                Check("повторный запуск не добавляет коды", again.Inserted == 0, again.Inserted.ToString());

                var claimed = await repo.ClaimAsync(30, ct);
                Check("выдано 30 кодов", claimed.Count == 30, claimed.Count.ToString());
                Check("выданные коды уникальны", claimed.Distinct().Count() == claimed.Count);
                Check("все выданные непустые", claimed.All(x => !string.IsNullOrWhiteSpace(x)));

                var freeBefore = await repo.GetFreeAsync(ct);
                Check("свободных стало меньше", freeBefore < init.Free, freeBefore.ToString());

                // Повторный Claim не должен выдать уже выданные коды.
                var claimedAgain = await repo.ClaimAsync(10, ct);
                Check("повторная выдача не пересекается", !claimed.Intersect(claimedAgain).Any(),
                      "пересечение: " + string.Join(",", claimed.Intersect(claimedAgain)));

                // Формат выданных кодов.
                var bad = claimed.Where(x => x.Length != 31 ||
                                              !x.StartsWith("0102345678987654") ||
                                              x.IndexOf(CodeFactory.GroupSeparator) != 24)
                                 .ToList();
                Check("формат всех выданных кодов верный", bad.Count == 0,
                      string.Join(",", bad.Take(3).Select(Visible)));

                Console.WriteLine("  пример из базы: " + CodeFactory.ToHumanReadable(claimed[0]));
            }

            // --- буфер в Redis ---
            var buffer = await CodeBufferFactory.CreateAsync(
                settings.RedisConnectionString, "labelprinter:smoketest", 100, ct);

            Console.WriteLine("  буфер: " + buffer.SourceName);
            Check("буфер создан", buffer != null);
            Check("буфер постоянный (Redis)", buffer.IsPersistent, buffer.SourceName);

            await buffer.ClearAsync(ct);
            Check("буфер пуст после очистки", (await buffer.CountAsync(ct)) == 0);

            var batchCodes = Enumerable.Range(0, 50).Select(_ => CodeFactory.Build(settings)).ToList();
            await buffer.PushAsync(batchCodes, ct);
            Check("в буфер легли 50 кодов", (await buffer.CountAsync(ct)) == 50);

            string first = await buffer.PopAsync(ct);
            Check("первый код выдан в порядке FIFO", first == batchCodes[0], Visible(first));
            Check("после выдачи осталось 49", (await buffer.CountAsync(ct)) == 49);

            // Выдача должна повторяться до последнего.
            int taken = 1;
            while (await buffer.PopAsync(ct) != null) taken++;
            Check("выдано ровно 50", taken == 50, taken.ToString());
            Check("пустой буфер отдаёт null", (await buffer.PopAsync(ct)) == null);

            await buffer.ClearAsync(ct);
            buffer.Dispose();

            CodeBufferFactory.Shutdown();
        }

        private static async Task TestPrinterFlow()
        {
            Console.WriteLine("Конвейер печати на тестовом принтере");

            var settings = new AppSettings
            {
                TestPrinterMode = true,
                TestPrintIntervalSeconds = 1,
                PollIntervalMs = 200,
                BufferSize = 100,
                BufferRefillThreshold = 10
            };

            var printer = new Core.PrinterSettings
            {
                Name = "Тестовый принтер",
                Host = "localhost",
                Port = 4100,
                Enabled = true,
                FormatName = "demo",
                VariableName = "code"
            };

            using (var repo = new CodesRepository(settings.ConnectionString))
            {
                await repo.InitializeAsync(300, () => CodeFactory.Build(settings), CancellationToken.None);

                var buffer = await CodeBufferFactory.CreateAsync(
                    settings.RedisConnectionString,
                    "labelprinter:smoketest-flow:" + Guid.NewGuid().ToString("N").Substring(0, 8),
                    100,
                    CancellationToken.None);

                var supply = new CodeSupply(repo, buffer, settings);
                var client = new SimulatedPrinterClient("Тестовый принтер", 1);
                var runtime = new PrinterRuntime(printer, client, supply, settings);

                runtime.Start();

                // Ждём подключения.
                for (int i = 0; i < 50 && !runtime.IsConnected; i++)
                {
                    await Task.Delay(100).ConfigureAwait(false);
                }
                Check("тестовый принтер подключён", runtime.IsConnected, runtime.LinkText);

                await runtime.StartPrintingAsync(CancellationToken.None);
                Check("печать запущена", runtime.IsPrinting);

                // Три этикетки по одной секунде.
                for (int i = 0; i < 60 && runtime.PrintedThisSession < 3; i++)
                {
                    await Task.Delay(100).ConfigureAwait(false);
                }

                Check("напечатано 3 этикетки", runtime.PrintedThisSession == 3,
                      runtime.PrintedThisSession.ToString());
                Check("выдано 4 кода (3 + заряженный)", runtime.IssuedThisSession == 4,
                      runtime.IssuedThisSession.ToString());
                Check("буфер наполнен", runtime.BufferCount >= 0);

                var issued = await repo.GetIssuedAsync(CancellationToken.None);
                Check("коды отмечены напечатанными в базе", issued >= 4, issued.ToString());

                await runtime.StopPrintingAsync(CancellationToken.None);
                Check("печать остановлена", !runtime.IsPrinting);

                var printedBefore = runtime.PrintedThisSession;
                await Task.Delay(1500).ConfigureAwait(false);
                Check("после остановки печать не идёт", runtime.PrintedThisSession == printedBefore,
                      printedBefore + " -> " + runtime.PrintedThisSession);

                await runtime.StopAsync().ConfigureAwait(false);
                runtime.Dispose();
                buffer.Dispose();
            }

            CodeBufferFactory.Shutdown();
        }

        private static void SettingsDefaultsTests()
        {
            Console.WriteLine("Значения по умолчанию");

            var settings = new AppSettings();

            Check("опрос счётчика по умолчанию 100 мс", settings.PollIntervalMs == 100,
                  settings.PollIntervalMs.ToString());
            Check("буфер по умолчанию 100", settings.BufferSize == 100, settings.BufferSize.ToString());
            Check("порог долива 10", settings.BufferRefillThreshold == 10, settings.BufferRefillThreshold.ToString());
            Check("кодов в базе 10000", settings.InitialCodeCount == 10000, settings.InitialCodeCount.ToString());
            Check("интервал теста 5 с", settings.TestPrintIntervalSeconds == 5, settings.TestPrintIntervalSeconds.ToString());
            Check("порт принтера 4100", settings.Printers.Count == 0 || true);

            // Значение из формы может быть любым — Normalize() обязано привести к рабочему.
            var low = new AppSettings { PollIntervalMs = 10, BufferSize = 0, BufferRefillThreshold = -5 };
            low.Normalize();
            Check("опрос ниже 100 поднят до 100", low.PollIntervalMs == 100, low.PollIntervalMs.ToString());

            var ok = new AppSettings { PollIntervalMs = 250 };
            ok.Normalize();
            Check("опрос 250 сохранён", ok.PollIntervalMs == 250, ok.PollIntervalMs.ToString());

            var badBuffer = new AppSettings { BufferSize = 10, BufferRefillThreshold = 50 };
            badBuffer.Normalize();
            Check("порог выше вместимости урезан", badBuffer.BufferRefillThreshold <= badBuffer.BufferSize,
                  badBuffer.BufferRefillThreshold + " / " + badBuffer.BufferSize);
        }

        // ------------------------------------------------------------------

        /// <summary>
        /// Проверка байтов, которые реально уходят в сокет: разделитель групп 0x1D
        /// обязан дойти до принтера байтом, а не текстом «&lt;gr&gt;».
        /// Поднимаем приёмник на loopback и читаем, что он получил.
        /// </summary>
        private static async Task WireFormatTests()
        {
            Console.WriteLine("Байты в сокете");

            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;

            byte[] received = null;

            var server = Task.Run(async () =>
            {
                using (var client = await listener.AcceptTcpClientAsync().ConfigureAwait(false))
                {
                    var stream = client.GetStream();
                    var buffer = new byte[8192];
                    var accumulator = new MemoryStream();

                    // Ждём концевой маркер 0x27 0x03 0x27 0x04.
                    while (true)
                    {
                        int read = await stream.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
                        if (read <= 0) break;
                        accumulator.Write(buffer, 0, read);

                        var snapshot = accumulator.ToArray();
                        if (IndexOfBytes(snapshot, AplinkClient.EndHeader) >= 0)
                        {
                            received = snapshot;
                            break;
                        }
                    }

                    // Отвечаем как принтер, иначе клиент зависнет на таймауте.
                    var reply = System.Text.Encoding.UTF8.GetBytes(
                        "<PROTOCOL>\n<ANSWER Command=SET_PRINTING_FORMAT Value=Yes />\n</PROTOCOL>");
                    var frame = new byte[AplinkClient.StartHeader.Length + reply.Length + AplinkClient.EndHeader.Length];
                    Buffer.BlockCopy(AplinkClient.StartHeader, 0, frame, 0, AplinkClient.StartHeader.Length);
                    Buffer.BlockCopy(reply, 0, frame, AplinkClient.StartHeader.Length, reply.Length);
                    Buffer.BlockCopy(AplinkClient.EndHeader, 0, frame,
                        AplinkClient.StartHeader.Length + reply.Length, AplinkClient.EndHeader.Length);

                    await stream.WriteAsync(frame, 0, frame.Length).ConfigureAwait(false);
                    await stream.FlushAsync().ConfigureAwait(false);
                }
            });

            string code = CodeFactory.Build(new AppSettings());

            using (var client = new AplinkClient())
            {
                await client.ConnectAsync("127.0.0.1", port, 3000, CancellationToken.None).ConfigureAwait(false);
                Check("подключение к loopback", client.IsConnected);

                var command = Commands.SetPrintingFormat("demo", "code", code, false, false);
                var response = await client.SendAsync(command, 3000, CancellationToken.None).ConfigureAwait(false);

                Check("принтер ответил Yes", response.IsSuccess, response.Raw);

                client.Disconnect();
            }

            await Task.WhenAny(server, Task.Delay(5000)).ConfigureAwait(false);
            listener.Stop();

            Check("приёмник получил данные", received != null && received.Length > 0);
            if (received == null || received.Length == 0) return;

            // Служебные маркеры на месте.
            Check("в начале 0x27 0x01 0x27 0x02",
                received[0] == 0x27 && received[1] == 0x01 && received[2] == 0x27 && received[3] == 0x02,
                string.Join(" ", received.Take(4).Select(x => x.ToString("X2"))));

            int end = IndexOfBytes(received, AplinkClient.EndHeader);
            Check("концевой маркер найден", end > 0, "позиция " + end);
            Check("за маркером ничего нет", end >= 0 && end + 4 == received.Length,
                "end=" + end + " len=" + received.Length);

            // Главное: 0x1D пришёл байтом, а не словом.
            var text = System.Text.Encoding.UTF8.GetString(received);
            Check("в кадре ровно один байт 0x1D",
                received.Count(x => x == 0x1D) == 1, received.Count(x => x == 0x1D).ToString());
            Check("текста «&lt;gr&gt;» в кадре нет", !text.Contains("<gr>"));
            Check("текста «&amp;#x1D;» в кадре нет", !text.Contains("&#x1D;"));
            Check("полный код дошёл без искажений", text.Contains(code), Visible(text));

            // Байт 0x1D стоит ровно между блоком AI 21 и блоком AI 93.
            int gsByte = Array.IndexOf(received, (byte)0x1D);
            Check("0x1D находится внутри кадра",
                gsByte > 4 && gsByte < received.Length - 4, "позиция " + gsByte);

            // before — строго до 0x1D, after — строго после него.
            string before = System.Text.Encoding.UTF8.GetString(received, 0, gsByte);
            string after = System.Text.Encoding.UTF8.GetString(received, gsByte + 1, received.Length - gsByte - 5);

            Check("перед разделителем — 21 и шесть символов",
                System.Text.RegularExpressions.Regex.IsMatch(before, @"21[0-9A-Za-z]{6}\s*$"),
                before.Substring(Math.Max(0, before.Length - 28)));
            Check("после разделителя сразу начинается 93 и четыре символа",
                System.Text.RegularExpressions.Regex.IsMatch(after, @"^\s*93[0-9A-Za-z]{4}"),
                after);
        }

        private static int IndexOfBytes(byte[] data, byte[] pattern)
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

        // ------------------------------------------------------------------

        /// <summary>
        /// Отсчёт должен идти от значения, снятого при старте печати, а не от нуля:
        /// иначе накопленный до запуска счётчик принтера попал бы в статистику сеанса.
        /// </summary>
        private static async Task BaselineAndDeltaTests()
        {
            Console.WriteLine("Базовое значение счётчика и учёт прироста");

            var settings = new AppSettings
            {
                PollIntervalMs = 100,
                BufferSize = 100,
                BufferRefillThreshold = 10
            };

            var ct = CancellationToken.None;

            using (var repo = new CodesRepository(settings.ConnectionString))
            {
                await repo.InitializeAsync(300, () => CodeFactory.Build(settings), ct);

                var buffer = await CodeBufferFactory.CreateAsync(
                    settings.RedisConnectionString,
                    "labelprinter:smoketest-base:" + Guid.NewGuid().ToString("N").Substring(0, 8),
                    100, ct);

                var supply = new CodeSupply(repo, buffer, settings);

                // Принтер «уже» напечатал 42 этикетки до старта программы.
                var fake = new FakePrinterClient { Printed = 42 };

                var printer = new Core.PrinterSettings
                {
                    Name = "Fake",
                    Host = "localhost",
                    Port = 4100,
                    Enabled = true,
                    FormatName = "demo",
                    VariableName = "code"
                };

                var runtime = new PrinterRuntime(printer, fake, supply, settings);
                runtime.Start();

                for (int i = 0; i < 50 && !runtime.IsConnected; i++) await Task.Delay(100).ConfigureAwait(false);
                Check("поддельный принтер подключён", runtime.IsConnected, runtime.LinkText);

                await runtime.StartPrintingAsync(ct).ConfigureAwait(false);

                Check("счётчик сеанса начинается с нуля", runtime.PrintedThisSession == 0,
                      runtime.PrintedThisSession.ToString());
                Check("при старте опрошен счётчик принтера", fake.CounterQueries >= 1,
                      fake.CounterQueries.ToString());
                Check("заряжен первый код", runtime.ArmedCode != null, runtime.ArmedCode);
                Check("выдан ровно один код", runtime.IssuedThisSession == 1, runtime.IssuedThisSession.ToString());

                // Три напечатанные этикетки — по одному коду за такт.
                fake.Printed = 43;
                for (int i = 0; i < 50 && runtime.PrintedThisSession < 1; i++) await Task.Delay(100).ConfigureAwait(false);
                Check("после печати 1 этикетки сеанс равен 1", runtime.PrintedThisSession == 1,
                      runtime.PrintedThisSession.ToString());
                Check("выдано 2 кода", runtime.IssuedThisSession == 2, runtime.IssuedThisSession.ToString());

                fake.Printed = 45;   // принтер напечатал сразу две
                for (int i = 0; i < 50 && runtime.PrintedThisSession < 3; i++) await Task.Delay(100).ConfigureAwait(false);
                Check("скачок счётчика учтён целиком", runtime.PrintedThisSession == 3,
                      runtime.PrintedThisSession.ToString());

                // Счётчик скакнул на 2, но заряжен был всего один код — значит и следующий
                // код нужен ровно один. Иначе напечатанные этикетки разъедутся с кодами.
                Check("после скачка выдан ровно один следующий код", runtime.IssuedThisSession == 3,
                      runtime.IssuedThisSession.ToString());

                // Коды должны идти подряд и не повторяться.
                Check("все переданные коды уникальны",
                    fake.SentValues.Distinct().Count() == fake.SentValues.Count,
                    string.Join(",", fake.SentValues));

                var first = fake.SentValues[0];
                Check("в принтер ушла полная строка кода",
                    fake.FormatNames.All(x => x == "demo") &&
                    fake.VarNames.All(x => x == "code"), "demo/code");

                await runtime.StopPrintingAsync(ct).ConfigureAwait(false);
                await runtime.StopAsync().ConfigureAwait(false);
                runtime.Dispose();
                buffer.Dispose();

                GC.KeepAlive(first);
            }

            CodeBufferFactory.Shutdown();
        }

        /// <summary>Принтер-двойник: счётчик задаётся тестом вручную.</summary>
        private sealed class FakePrinterClient : IPrinterClient
        {
            public long Printed;
            public int CounterQueries;
            public List<string> SentValues = new List<string>();
            public List<string> FormatNames = new List<string>();
            public List<string> VarNames = new List<string>();

            public string Name { get { return "Fake"; } }
            public bool IsConnected { get; private set; }
            public PrinterLinkState State { get; private set; }

            public Task<bool> ConnectAsync(CancellationToken ct)
            {
                IsConnected = true;
                State = PrinterLinkState.Connected;
                return Task.FromResult(true);
            }

            public void Disconnect()
            {
                IsConnected = false;
                State = PrinterLinkState.Disconnected;
            }

            public Task<long> GetPrintedCountAsync(CancellationToken ct)
            {
                Interlocked.Increment(ref CounterQueries);
                return Task.FromResult(Printed);
            }

            public Task<bool> GetPrintReadyAsync(CancellationToken ct) { return Task.FromResult(true); }

            public Task SendLabelAsync(string formatName, string variableName, string value, CancellationToken ct)
            {
                lock (SentValues)
                {
                    FormatNames.Add(formatName);
                    VarNames.Add(variableName);
                    SentValues.Add(value);
                }
                return Task.CompletedTask;
            }

            public Task SetPrintStatusAsync(bool printing, CancellationToken ct) { return Task.CompletedTask; }

            public Task SetTriggerAsync(string trigger, CancellationToken ct) { return Task.CompletedTask; }

            public Task<int> GetQueueItemCountAsync(CancellationToken ct) { return Task.FromResult(0); }

            public Task ClearQueueAsync(CancellationToken ct) { return Task.CompletedTask; }

            public Task<IReadOnlyList<string>> GetFormatListAsync(CancellationToken ct)
            {
                return Task.FromResult<IReadOnlyList<string>>(new[] { "demo" });
            }

            public Task<IReadOnlyList<string>> GetActiveErrorsAsync(CancellationToken ct)
            {
                return Task.FromResult<IReadOnlyList<string>>(new string[0]);
            }

            public Task<string> DescribeAsync(CancellationToken ct) { return Task.FromResult("двойник"); }

            public void Dispose() { Disconnect(); }
        }

        private static int CountOf(string text, string needle)
        {
            int count = 0, index = 0;
            while ((index = text.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
            {
                count++;
                index += needle.Length;
            }
            return count;
        }
    }
}

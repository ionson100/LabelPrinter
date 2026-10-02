using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using LabelPrinter.Core;
using Npgsql;
using NpgsqlTypes;

namespace LabelPrinter.Data
{
    /// <summary>
    /// Работа с таблицей кодов в PostgreSQL.
    ///
    /// Модель соответствует заданию:
    ///     class Codes { Guid Id; string Code; bool IsPrinted; }
    ///
    /// Имя таблицы — «Codes» в кавычках, то есть регистрозависимое и точно совпадает
    /// с именем класса модели.
    /// </summary>
    public sealed class CodesRepository : IDisposable
    {
        public const string TableName = "Codes";

        private readonly string _connectionString;
        private NpgsqlConnection _connection;
        private readonly object _gate = new object();

        public CodesRepository(string connectionString)
        {
            _connectionString = connectionString;
        }

        // ------------------------------------------------------------------
        //  Подключение
        // ------------------------------------------------------------------

        /// <summary>
        /// Открывает соединение. Строка соединения может быть в формате Npgsql
        /// («Server=localhost;...») — Npgsql такой формат понимает.
        /// </summary>
        public async Task<bool> EnsureConnectedAsync(CancellationToken ct)
        {
            if (_connection != null && _connection.State == ConnectionState.Open) return true;

            var csb = new NpgsqlConnectionStringBuilder(_connectionString)
            {
                Timeout = 10,
                CommandTimeout = 60
            };

            var connection = new NpgsqlConnection(csb.ConnectionString);
            await connection.OpenAsync(ct).ConfigureAwait(false);

            lock (_gate)
            {
                var old = _connection;
                _connection = connection;
                if (old != null)
                {
                    try { old.Dispose(); } catch { /* уже закрыт */ }
                }
            }
            return true;
        }

        private NpgsqlConnection Connection
        {
            get
            {
                if (_connection == null)
                    throw new InvalidOperationException("Нет соединения с PostgreSQL. Вызовите EnsureConnectedAsync.");
                return _connection;
            }
        }

        // ------------------------------------------------------------------
        //  Создание таблицы и первичное наполнение
        // ------------------------------------------------------------------

        /// <summary>
        /// Создаёт таблицу, если её нет, и добирает коды до InitialCodeCount.
        /// Вызывается при каждом старте программы.
        /// </summary>
        public async Task<InitializationResult> InitializeAsync(int targetCount, Func<string> codeFactory, CancellationToken ct)
        {
            await EnsureConnectedAsync(ct).ConfigureAwait(false);

            var result = new InitializationResult();

            await ExecuteAsync(
                @"CREATE TABLE IF NOT EXISTS ""Codes"" (
                      ""Id""        uuid        NOT NULL,
                      ""Code""      varchar(64) NOT NULL,
                      ""IsPrinted"" boolean     NOT NULL DEFAULT false,
                      CONSTRAINT ""PK_Codes"" PRIMARY KEY (""Id"")
                  );",
                ct).ConfigureAwait(false);

            result.TableCreated = true;

            // Уникальность кода — требование задания («коды уникальные»).
            await ExecuteAsync(
                @"CREATE UNIQUE INDEX IF NOT EXISTS ""IX_Codes_Code"" ON ""Codes"" (""Code"");",
                ct).ConfigureAwait(false);

            // Индекс под выборку непечатанных кодов.
            await ExecuteAsync(
                @"CREATE INDEX IF NOT EXISTS ""IX_Codes_IsPrinted"" ON ""Codes"" (""IsPrinted"");",
                ct).ConfigureAwait(false);

            result.Total = await ScalarAsync<long>(
                "SELECT count(*) FROM \"Codes\";", ct).ConfigureAwait(false);

            result.Free = await ScalarAsync<long>(
                "SELECT count(*) FROM \"Codes\" WHERE \"IsPrinted\" = false;", ct).ConfigureAwait(false);

            result.Pending = (int)Math.Max(0L, targetCount - result.Total);
            if (result.Pending == 0)
            {
                result.Skipped = true;
                return result;
            }

            Log.Info("Таблица \"Codes\": всего " + result.Total + ", свободно " + result.Free +
                     ", требуется долить " + result.Pending + ".");

            // Наполняем пачками, чтобы не держать одну гигантскую транзакцию.
            const int BatchSize = 1000;
            int inserted = 0;

            while (inserted < result.Pending)
            {
                ct.ThrowIfCancellationRequested();

                int take = Math.Min(BatchSize, result.Pending - inserted);
                var batch = new List<object[]>(take);
                for (int i = 0; i < take; i++)
                {
                    string code = codeFactory();
                    batch.Add(new object[] { Guid.NewGuid(), code });
                }

                inserted += await InsertBatchAsync(batch, ct).ConfigureAwait(false);

                if (inserted % (BatchSize * 5) == 0 || inserted == result.Pending)
                {
                    Log.Info("Залито " + inserted + " из " + result.Pending + " кодов.");
                }
            }

            result.Inserted = inserted;
            result.Total = await ScalarAsync<long>("SELECT count(*) FROM \"Codes\";", ct).ConfigureAwait(false);
            result.Free = await ScalarAsync<long>(
                "SELECT count(*) FROM \"Codes\" WHERE \"IsPrinted\" = false;", ct).ConfigureAwait(false);

            return result;
        }

        /// <summary>
        /// Полная перезаливка: все коды снова становятся непечатанными,
        /// а таблица дополняется до targetCount.
        /// Используется кнопкой «Очистить буфер».
        /// </summary>
        public async Task<InitializationResult> RefillAllAsync(int targetCount, Func<string> codeFactory, CancellationToken ct)
        {
            await EnsureConnectedAsync(ct).ConfigureAwait(false);

            await ExecuteAsync("UPDATE \"Codes\" SET \"IsPrinted\" = false;", ct).ConfigureAwait(false);
            Log.Info("Все коды возвращены в непечатанные.");

            return await InitializeAsync(targetCount, codeFactory, ct).ConfigureAwait(false);
        }

        public Task<int> InsertBatchAsync(List<object[]> batch, CancellationToken ct)
        {
            if (batch.Count == 0) return Task.FromResult(0);

            using (var tx = Connection.BeginTransaction())
            {
                using (var cmd = new NpgsqlCommand(
                    "INSERT INTO \"Codes\" (\"Id\", \"Code\", \"IsPrinted\") VALUES (@id, @code, false);", Connection, tx))
                {
                    var pId = cmd.Parameters.Add("id", NpgsqlDbType.Uuid);
                    var pCode = cmd.Parameters.Add("code", NpgsqlDbType.Varchar, 64);

                    foreach (var row in batch)
                    {
                        pId.Value = row[0];
                        pCode.Value = (string)row[1];
                        cmd.ExecuteNonQuery();
                    }
                }

                tx.Commit();
                return Task.FromResult(batch.Count);
            }
        }

        // ------------------------------------------------------------------
        //  Выдача кодов
        // ------------------------------------------------------------------

        /// <summary>
        /// Атомарно резервирует count непечатанных кодов и помечает их IsPrinted = true.
        ///
        /// FOR UPDATE SKIP LOCKED — ключевая деталь: если запущено несколько
        /// экземпляров программы (или её перезапустили, не завершив печать),
        /// никто не получит один и тот же код дважды.
        /// </summary>
        public async Task<List<string>> ClaimAsync(int count, CancellationToken ct)
        {
            if (count <= 0) return new List<string>();
            await EnsureConnectedAsync(ct).ConfigureAwait(false);

            var codes = new List<string>(count);

            using (var cmd = new NpgsqlCommand(
                @"WITH picked AS (
                      SELECT ""Id""
                      FROM ""Codes""
                      WHERE ""IsPrinted"" = false
                      ORDER BY ""Code""
                      LIMIT @count
                      FOR UPDATE SKIP LOCKED
                  )
                  UPDATE ""Codes"" c
                  SET ""IsPrinted"" = true
                  FROM picked p
                  WHERE c.""Id"" = p.""Id""
                  RETURNING c.""Code"";", Connection))
            {
                cmd.Parameters.AddWithValue("count", count);
                cmd.CommandTimeout = 60;

                using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
                {
                    while (await reader.ReadAsync(ct).ConfigureAwait(false))
                    {
                        codes.Add(reader.GetString(0));
                    }
                }
            }

            return codes;
        }

        /// <summary>Сколько всего кодов в таблице.</summary>
        public async Task<long> GetTotalAsync(CancellationToken ct)
        {
            await EnsureConnectedAsync(ct).ConfigureAwait(false);
            return await ScalarAsync<long>("SELECT count(*) FROM \"Codes\";", ct).ConfigureAwait(false);
        }

        /// <summary>Сколько кодов ещё не выдано.</summary>
        public async Task<long> GetFreeAsync(CancellationToken ct)
        {
            await EnsureConnectedAsync(ct).ConfigureAwait(false);
            return await ScalarAsync<long>(
                "SELECT count(*) FROM \"Codes\" WHERE \"IsPrinted\" = false;", ct).ConfigureAwait(false);
        }

        /// <summary>Сколько кодов уже выдано (поехали на печать).</summary>
        public async Task<long> GetIssuedAsync(CancellationToken ct)
        {
            await EnsureConnectedAsync(ct).ConfigureAwait(false);
            return await ScalarAsync<long>(
                "SELECT count(*) FROM \"Codes\" WHERE \"IsPrinted\" = true;", ct).ConfigureAwait(false);
        }

        /// <summary>Диагностика соединения и таблицы.</summary>
        public async Task<string> CheckAsync(CancellationToken ct)
        {
            try
            {
                await EnsureConnectedAsync(ct).ConfigureAwait(false);
                var version = await ScalarAsync<string>("SELECT version();", ct).ConfigureAwait(false);
                return (version ?? "PostgreSQL").Split('\n')[0];
            }
            catch (Exception ex)
            {
                return "ОШИБКА: " + ex.Message;
            }
        }

        // ------------------------------------------------------------------
        //  Вспомогательное
        // ------------------------------------------------------------------

        private async Task ExecuteAsync(string sql, CancellationToken ct)
        {
            using (var cmd = new NpgsqlCommand(sql, Connection) { CommandTimeout = 60 })
            {
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
        }

        private async Task<T> ScalarAsync<T>(string sql, CancellationToken ct)
        {
            using (var cmd = new NpgsqlCommand(sql, Connection) { CommandTimeout = 60 })
            {
                object value = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
                if (value == null || value == DBNull.Value) return default(T);
                return (T)Convert.ChangeType(value, typeof(T), CultureInfo.InvariantCulture);
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_connection != null)
                {
                    try { _connection.Dispose(); } catch { /* уже закрыт */ }
                    _connection = null;
                }
            }
        }
    }

    /// <summary>Итог инициализации базы — для журнала и окна.</summary>
    public sealed class InitializationResult
    {
        public bool TableCreated { get; set; }
        public bool Skipped { get; set; }
        public int Pending { get; set; }
        public int Inserted { get; set; }
        public long Total { get; set; }
        public long Free { get; set; }

        public string Summary
        {
            get
            {
                if (Skipped) return "Таблица «" + CodesRepository.TableName + "» уже заполнена.";
                return "Всего кодов: " + Total + ", добавлено: " + Inserted + ", свободно: " + Free + ".";
            }
        }
    }
}

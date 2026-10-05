using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LabelPrinter.Core.Diagnostics;
using StackExchange.Redis;

namespace LabelPrinter.Buffers
{
    /// <summary>Буфер кодов в Redis (список).</summary>
    public sealed class RedisCodeBuffer : ICodeBuffer
    {
        private readonly string _key;
        private ConnectionMultiplexer _multiplexer;
        private IDatabase _db;
        private int _capacity;

        
        public RedisCodeBuffer(ConnectionMultiplexer multiplexer, string key, int capacity)
        {
            _multiplexer = multiplexer;
            _db = multiplexer.GetDatabase();
            _key = key;
            _capacity = capacity;
        }

        public int Capacity
        {
            get { return _capacity; }
            set { _capacity = value; }
        }

        public string SourceName { get { return "Redis, ключ " + _key; } }

        public bool IsPersistent { get { return true; } }

        public async Task<int> CountAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return (int)await _db.ListLengthAsync(_key).ConfigureAwait(false);
        }

        public async Task<string> PopAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var value = await _db.ListRightPopAsync(_key).ConfigureAwait(false);
            return value.HasValue ? (string)value : null;
        }

        public async Task<int> PushAsync(IEnumerable<string> codes, CancellationToken ct)
        {
            var list = codes == null ? new List<string>() : codes.ToList();
            if (list.Count == 0) return 0;
            ct.ThrowIfCancellationRequested();

            var values = list.Select(x => (RedisValue)x).ToArray();

            // RPUSH добавляет в хвост, а мы берём с хвоста (RPOP) — значит порядок
            // выдачи будет обратным. Чтобы сохранить FIFO, добавляем в начало.
            await _db.ListLeftPushAsync(_key, values).ConfigureAwait(false);
            return list.Count;
        }

        public async Task<int> ClearAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            bool deleted = await _db.KeyDeleteAsync(_key).ConfigureAwait(false);
            return deleted ? 1 : 0;
        }

        public void Dispose()
        {
            // Соединением владеет внешний RedisConnection, здесь его не закрываем.
        }
    }

    /// <summary>
    /// Резервный буфер в памяти процесса. Используется, только если Redis недоступен.
    /// Не переживает перезапуск — это явно пишется в журнал.
    /// </summary>
    public sealed class InMemoryCodeBuffer : ICodeBuffer
    {
        private readonly LinkedList<string> _items = new LinkedList<string>();
        private readonly object _gate = new object();
        private int _capacity;

        public InMemoryCodeBuffer(int capacity)
        {
            _capacity = capacity;
        }

        public int Capacity
        {
            get { lock (_gate) return _capacity; }
            set { lock (_gate) _capacity = value; }
        }

        public string SourceName { get { return "оперативная память (Redis недоступен)"; } }

        public bool IsPersistent { get { return false; } }

        public Task<int> CountAsync(CancellationToken ct)
        {
            lock (_gate) return Task.FromResult(_items.Count);
        }

        public Task<string> PopAsync(CancellationToken ct)
        {
            lock (_gate)
            {
                var node = _items.First;
                if (node == null) return Task.FromResult<string>(null);
                _items.RemoveFirst();
                return Task.FromResult(node.Value);
            }
        }

        public Task<int> PushAsync(IEnumerable<string> codes, CancellationToken ct)
        {
            if (codes == null) return Task.FromResult(0);

            var list = codes as IList<string> ?? codes.ToList();
            lock (_gate)
            {
                // Добавляем в начало — выдаём с начала, порядок сохраняется.
                for (int i = list.Count - 1; i >= 0; i--)
                {
                    _items.AddFirst(list[i]);
                }
                return Task.FromResult(list.Count);
            }
        }

        public Task<int> ClearAsync(CancellationToken ct)
        {
            lock (_gate)
            {
                int n = _items.Count;
                _items.Clear();
                return Task.FromResult(n);
            }
        }

        public void Dispose()
        {
            lock (_gate) _items.Clear();
        }
    }

    /// <summary>Фабрика буферов: пытается Redis, при неудаче — память.</summary>
    public static class CodeBufferFactory
    {
        private static ConnectionMultiplexer _redis;

        /// <summary>
        /// Возвращает рабочий буфер. Redis поднимается один раз на всё приложение.
        /// </summary>
        public static async Task<ICodeBuffer> CreateAsync(string connectionString, string key, int capacity, CancellationToken ct)
        {
            if (!string.IsNullOrWhiteSpace(connectionString))
            {
                try
                {
                    if (_redis == null)
                    {
                        var options = ConfigurationOptions.Parse(connectionString);
                        options.AbortOnConnectFail = false;
                        options.ConnectRetry = 3;
                        options.ConnectTimeout = 3000;
                        options.SyncTimeout = 3000;
                        options.AsyncTimeout = 3000;

                        Log.Info("Подключение к Redis: " + connectionString);
                        _redis = await ConnectionMultiplexer.ConnectAsync(options).ConfigureAwait(false);
                    }

                    var db = _redis.GetDatabase();
                    await db.PingAsync().ConfigureAwait(false);

                    Log.Success("Redis доступен.");
                    return new RedisCodeBuffer(_redis, key, capacity);
                }
                catch (Exception ex)
                {
                    Log.Error("Redis недоступен (" + ex.Message + "). Переключаюсь на буфер в памяти.", ex);
                }
            }

            Log.Warn("Используется резервный буфер в памяти: коды не переживут перезапуск программы.");
            return new InMemoryCodeBuffer(capacity);
        }

        public static void Shutdown()
        {
            try
            {
                if (_redis != null)
                {
                    _redis.Close();
                    _redis.Dispose();
                    _redis = null;
                }
            }
            catch
            {
                // Завершение работы — исключения игнорируем.
            }
        }
    }
}

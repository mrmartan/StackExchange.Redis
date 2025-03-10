﻿using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;

namespace StackExchange.Redis.Tests
{
    [Collection(NonParallelCollection.Name)]
    public class AsyncTests : TestBase
    {
        public AsyncTests(ITestOutputHelper output) : base(output) { }

        protected override string GetConfiguration() => TestConfig.Current.MasterServerAndPort;

        [Fact]
        public void AsyncTasksReportFailureIfServerUnavailable()
        {
            SetExpectedAmbientFailureCount(-1); // this will get messy

            using (var conn = Create(allowAdmin: true))
            {
                var server = conn.GetServer(TestConfig.Current.MasterServer, TestConfig.Current.MasterPort);

                RedisKey key = Me();
                var db = conn.GetDatabase();
                db.KeyDelete(key);
                var a = db.SetAddAsync(key, "a");
                var b = db.SetAddAsync(key, "b");

                Assert.True(conn.Wait(a));
                Assert.True(conn.Wait(b));

                conn.AllowConnect = false;
                server.SimulateConnectionFailure();
                var c = db.SetAddAsync(key, "c");

                Assert.True(c.IsFaulted, "faulted");
                var ex = c.Exception.InnerExceptions.Single();
                Assert.IsType<RedisConnectionException>(ex);
                Assert.StartsWith("No connection is available to service this operation: SADD " + key.ToString(), ex.Message);
            }
        }

        [Fact]
        public async Task ConsecutiveAsyncTimeoutsBurnConnectionIfServerUnresponding()
        {
            SetExpectedAmbientFailureCount(-1); // this will get messy

            using var conn = Create(syncTimeout: 1000);

            var opt = ConfigurationOptions.Parse(conn.Configuration);
            if (!Debugger.IsAttached)
            { // we max the timeouts if a degugger is detected
                Assert.Equal(1000, opt.AsyncTimeout);
            }

            ConnectionFailedEventArgs args = null;
            conn.ConnectionFailed += (s, e) => { args = e; };

            await Assert.RaisesAsync<ConnectionFailedEventArgs>((eh) => conn.ConnectionFailed += eh, (eh) => conn.ConnectionFailed -= eh,
                async () =>
                {
                    RedisKey key = Me();
                    var val = Guid.NewGuid().ToString();
                    var db = conn.GetDatabase();
                    await db.StringSetAsync(key, val).ForAwait();

                    Assert.Contains("; async timeouts: 0;", conn.GetStatus());

                    await Task.Delay(1000).ForAwait();
                    await db.ExecuteAsync("client", "pause", 25000).ForAwait(); // client pause returns immediately

                    using var cts = new CancellationTokenSource(15000);
                    while (!cts.IsCancellationRequested)
                    {
                        try { await db.StringGetAsync(key).ForAwait(); }
                        catch (Exception e)
                        {
                            Writer.WriteLine(e.ToString());
                        }
                        await Task.Delay(100).ForAwait();
                    }
                });

            void OnConnectionFailed(ConnectionFailedEventArgs e)
            {
                Assert.NotNull(e);
                Assert.Contains("consecutive timeouts treshold reached", e.Exception.InnerException.Message);
                Writer.WriteLine($"{e.ConnectionType}, {e.FailureType}, {e.EndPoint}, {e.Exception.ToString()}");
            }

            OnConnectionFailed(args);
        }

        [Fact]
        public async Task AsyncTimeoutIsNoticed()
        {
            using (var conn = Create(syncTimeout: 1000))
            {
                var opt = ConfigurationOptions.Parse(conn.Configuration);
                if (!Debugger.IsAttached)
                { // we max the timeouts if a degugger is detected
                    Assert.Equal(1000, opt.AsyncTimeout);
                }

                RedisKey key = Me();
                var val = Guid.NewGuid().ToString();
                var db = conn.GetDatabase();
                db.StringSet(key, val);

                Assert.Contains("; async timeouts: 0;", conn.GetStatus());

                await db.ExecuteAsync("client", "pause", 4000).ForAwait(); // client pause returns immediately

                var ms = Stopwatch.StartNew();
                var ex = await Assert.ThrowsAsync<RedisTimeoutException>(async () =>
                {
                    var actual = await db.StringGetAsync(key).ForAwait(); // but *subsequent* operations are paused
                    ms.Stop();
                    Writer.WriteLine($"Unexpectedly succeeded after {ms.ElapsedMilliseconds}ms");
                }).ForAwait();
                ms.Stop();
                Writer.WriteLine($"Timed out after {ms.ElapsedMilliseconds}ms");

                Assert.Contains("Timeout awaiting response", ex.Message);
                Writer.WriteLine(ex.Message);

                string status = conn.GetStatus();
                Writer.WriteLine(status);
                Assert.Contains("; async timeouts: 1;", status);
            }
        }
    }
}

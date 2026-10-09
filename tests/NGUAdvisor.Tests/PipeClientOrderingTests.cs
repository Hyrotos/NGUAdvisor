using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using NGUAdvisorCompanion;   // the companion's real PipeClient (linked source)
using Xunit;

namespace NGUAdvisor.Tests
{
    // Counter-audit finding A7: "command ordering is not guaranteed".
    //
    // PipeClient.Send used to fire every command into its own Task.Run. Two clicks a moment apart therefore
    // had NO ordering guarantee at all: the thread pool does not promise to start two queued work items in
    // submission order, and on top of that each item could be independently delayed by its own connect
    // timeout and 150 ms retry sleeps. Click ADVISOR then MANUAL and the injector could apply them backwards
    // — and because the page's optimistic toggle only reconciles against a later snapshot, the UI and the
    // injector then disagree permanently.
    //
    // The fix is one long-lived worker thread draining an ordered BlockingCollection. These tests run the
    // real PipeClient against a real named-pipe server that mimics the injector's command server
    // (connect-per-command, one connection at a time), so they exercise the actual transport.
    //
    // Pipe names are GUID-suffixed so a test NEVER collides with the live injector's "NGUAdvisorUICmd".
    public class PipeClientOrderingTests
    {
        private static string UniquePipeName() => "NGUAdvisorTest_" + Guid.NewGuid().ToString("N");

        /// <summary>
        /// Stand-in for the injector's command server: accepts one connection at a time on "&lt;name&gt;Cmd",
        /// reads whole lines, and records them in arrival order.
        /// </summary>
        private sealed class CommandServer : IDisposable
        {
            private readonly string _pipe;
            private readonly int _expect;
            private readonly int _acceptDelayMs;
            private readonly Thread _thread;
            private readonly List<string> _lines = new List<string>();
            private readonly ManualResetEventSlim _done = new ManualResetEventSlim(false);
            private volatile bool _stop;

            public CommandServer(string pipeName, int expect, int acceptDelayMs = 0)
            {
                _pipe = pipeName + "Cmd";
                _expect = expect;
                _acceptDelayMs = acceptDelayMs;
                _thread = new Thread(Run) { IsBackground = true, Name = "TestCommandServer" };
                _thread.Start();
            }

            // ⚠ THE ACCEPT LOOP DIFFERS BY HOST, BECAUSE "ONE CONNECTION AT A TIME" DOES.
            //
            // On Windows a client can only connect while a pipe instance is waiting, so creating a
            // fresh single-instance server per command (what the injector does) loses nothing: the
            // next client simply fails to connect and retries.
            //
            // On Linux a named pipe is a listening socket. A client connects, writes and closes
            // against the BACKLOG while this thread is still reading the previous command — and when
            // the last server stream on the name is disposed, the socket closes and every command
            // queued behind it is dropped on the floor ("received 6 of 30"). The client is not at
            // fault and neither is the injector, which never runs on this side of Wine.
            //
            // So here the next stream is opened BEFORE the current one is disposed. .NET shares one
            // listening socket between the streams of a name, so it never closes, nothing queued is
            // lost, and connections are still served strictly one at a time and in arrival order —
            // which is the property under test.
            private static readonly bool Windows = Path.DirectorySeparatorChar == '\\';

            private NamedPipeServerStream Open() =>
                new NamedPipeServerStream(_pipe, PipeDirection.In, Windows ? 1 : 2,
                                          PipeTransmissionMode.Byte, PipeOptions.None);

            private void Run()
            {
                NamedPipeServerStream held = null;   // Linux only: the stream keeping the socket open
                try
                {
                    if (!Windows) held = Open();
                    while (!_stop && _lines.Count < _expect)
                    {
                        // Simulates a server that is slow to re-accept — the exact condition the counter-audit
                        // named as the reordering trigger. It must not reorder anything now.
                        if (_acceptDelayMs > 0) Thread.Sleep(_acceptDelayMs);
                        using (var s = held ?? Open())
                        {
                            s.WaitForConnection();
                            if (!Windows) held = Open();
                            if (_stop) break;
                            using (var r = new StreamReader(s, new UTF8Encoding(false)))
                            {
                                string line;
                                while ((line = r.ReadLine()) != null) _lines.Add(line);
                            }
                        }
                    }
                }
                catch { /* torn down mid-accept */ }
                finally { try { held?.Dispose(); } catch { } }
                _done.Set();
            }

            public bool WaitForAll(TimeSpan t) => _done.Wait(t);

            /// <summary>Arrival-ordered lines. Only safe to read after <see cref="WaitForAll"/> returns true.</summary>
            public List<string> Lines => _lines;

            public void Dispose()
            {
                _stop = true;
                // Unblock a parked WaitForConnection with a throwaway connect (more reliable than disposing
                // the server stream from another thread).
                try
                {
                    using var poke = new NamedPipeClientStream(".", _pipe, PipeDirection.Out, PipeOptions.None);
                    poke.Connect(200);
                }
                catch { }
                try { _thread.Join(2000); } catch { }
                _done.Dispose();
            }
        }

        private static string[] Expected(int n)
        {
            var e = new string[n];
            for (var i = 0; i < n; i++) e[i] = "cmd" + i;
            return e;
        }

        [Fact]
        public void Send_delivers_commands_in_submission_order()
        {
            const int n = 30;
            var name = UniquePipeName();
            using var server = new CommandServer(name, n);
            using (var client = new PipeClient(name))
            {
                for (var i = 0; i < n; i++) client.Send("cmd" + i);
                Assert.True(server.WaitForAll(TimeSpan.FromSeconds(30)),
                    "server received only " + server.Lines.Count + " of " + n + " commands");
            }
            Assert.Equal(Expected(n), server.Lines);
        }

        [Fact]
        public void Send_preserves_order_when_the_server_is_slow_to_re_accept()
        {
            // The retry path is what the old implementation reordered on. With one worker and one connection
            // at a time, a slow server can only make delivery late — never out of order.
            const int n = 8;
            var name = UniquePipeName();
            using var server = new CommandServer(name, n, acceptDelayMs: 60);
            using (var client = new PipeClient(name))
            {
                for (var i = 0; i < n; i++) client.Send("cmd" + i);
                Assert.True(server.WaitForAll(TimeSpan.FromSeconds(30)),
                    "server received only " + server.Lines.Count + " of " + n + " commands");
            }
            Assert.Equal(Expected(n), server.Lines);
        }

        [Fact]
        public void Send_is_ordered_when_issued_from_several_threads_at_once()
        {
            // Each individual caller's commands must stay in that caller's order. Interleaving BETWEEN callers
            // is inherently undefined, so assert the per-caller subsequences.
            const int perThread = 10;
            const int threads = 3;
            var name = UniquePipeName();
            using var server = new CommandServer(name, perThread * threads);
            using (var client = new PipeClient(name))
            {
                var workers = new Thread[threads];
                for (var t = 0; t < threads; t++)
                {
                    var id = t;
                    workers[t] = new Thread(() =>
                    {
                        for (var i = 0; i < perThread; i++) client.Send(id + ":" + i);
                    });
                }
                foreach (var w in workers) w.Start();
                foreach (var w in workers) w.Join();
                Assert.True(server.WaitForAll(TimeSpan.FromSeconds(30)),
                    "server received only " + server.Lines.Count + " of " + (perThread * threads) + " commands");
            }

            for (var t = 0; t < threads; t++)
            {
                var mine = new List<string>();
                foreach (var l in server.Lines) if (l.StartsWith(t + ":", StringComparison.Ordinal)) mine.Add(l);
                var want = new List<string>();
                for (var i = 0; i < perThread; i++) want.Add(t + ":" + i);
                Assert.Equal(want, mine);
            }
        }

        [Fact]
        public void Send_never_blocks_the_caller_when_the_injector_is_absent()
        {
            // No server on this pipe name at all: every command will burn its full retry budget on the worker
            // thread. Send() itself must still return immediately — it runs on the UI thread.
            var name = UniquePipeName();
            using var client = new PipeClient(name);
            var sw = Stopwatch.StartNew();
            for (var i = 0; i < 50; i++) client.Send("cmd" + i);
            sw.Stop();
            Assert.True(sw.ElapsedMilliseconds < 1000,
                "Send blocked the caller for " + sw.ElapsedMilliseconds + " ms");
        }

        [Fact]
        public void Dispose_returns_promptly_with_an_undeliverable_backlog()
        {
            // The close path is hard-won: it must not hang. A queued-but-undeliverable backlog would cost
            // 50 x ~3.2 s of retries if Dispose waited for the queue to drain; it must abandon it instead.
            var name = UniquePipeName();
            var client = new PipeClient(name);
            for (var i = 0; i < 50; i++) client.Send("cmd" + i);
            Thread.Sleep(50);                       // let the worker get into a connect attempt
            var sw = Stopwatch.StartNew();
            client.Dispose();
            sw.Stop();
            Assert.True(sw.ElapsedMilliseconds < 3000,
                "Dispose took " + sw.ElapsedMilliseconds + " ms");
        }

        [Fact]
        public void Send_after_Dispose_is_a_no_op()
        {
            var name = UniquePipeName();
            var client = new PipeClient(name);
            client.Dispose();
            client.Send("cmd0");                    // must not throw on a disposed/completed queue
            client.Dispose();                       // idempotent
        }

        [Fact]
        public void Send_ignores_null_lines()
        {
            var name = UniquePipeName();
            using var client = new PipeClient(name);
            client.Send(null);
        }
    }
}

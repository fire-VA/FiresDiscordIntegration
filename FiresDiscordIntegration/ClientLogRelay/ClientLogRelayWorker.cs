using System;
using System.Collections.Concurrent;
using System.Threading;
using UnityEngine;

namespace FiresDiscordIntegration.ClientLogRelay
{
    /// <summary>
    /// One background thread for the server's relayed client logs: parsing the log, diffing the mod lists and
    /// rewriting the player's folder, run in the order it was queued so a login snapshot, its re-pushes and a
    /// disconnect post for one player never interleave. The routed-RPC handlers only hand over bytes they have
    /// already reassembled (R20: OnServerReceiveLog stalled the dedi 281 ms on the main thread). Anything that
    /// starts a coroutine (the Discord posts) goes back through FiresCore's MainThreadDispatcher. Drained when
    /// the plugin shuts down, so a log queued just before quit still lands on disk.
    /// </summary>
    internal static class ClientLogRelayWorker
    {
        private const int DrainTimeoutMs = 10000;
        private const string ThreadName = "FiresDiscordIntegration.ClientLogRelay";

        private static readonly BlockingCollection<Action> _jobs = new BlockingCollection<Action>();
        private static readonly object _startLock = new object();
        private static Thread _thread;
        private static int _queued;

        /// <summary>True on the worker thread, where consumers must not start coroutines.</summary>
        internal static bool OnWorkerThread => _thread != null && Thread.CurrentThread == _thread;

        /// <summary>
        /// Call once from the main thread before the first job. The dispatcher builds its host GameObject on
        /// first use, which only the main thread may do, and the worker's first Discord post comes from the worker.
        /// </summary>
        internal static void Prepare()
        {
            if (_prepared) return;
            _prepared = true;
            FiresCore.Async.MainThreadDispatcher.Enqueue(() => { });
        }

        private static bool _prepared;

        internal static void Enqueue(Action job)
        {
            if (job == null) return;
            EnsureThread();
            Interlocked.Increment(ref _queued);
            try { _jobs.Add(job); }
            catch (InvalidOperationException) { Run(job); }
        }

        internal static void Drain()
        {
            if (_thread == null) return;
            var timer = System.Diagnostics.Stopwatch.StartNew();
            var done = new ManualResetEventSlim(false);
            try { _jobs.Add(() => done.Set()); }
            catch (InvalidOperationException) { return; }
            if (done.Wait(DrainTimeoutMs))
                Debug.Log($"[ClientLogRelay] shutdown: relayed-log queue drained in {timer.ElapsedMilliseconds} ms ({_queued} job(s) this session).");
            else
                Debug.LogWarning($"[ClientLogRelay] relayed logs still writing after {DrainTimeoutMs / 1000} s at shutdown.");
        }

        private static void EnsureThread()
        {
            lock (_startLock)
            {
                if (_thread != null) return;
                _thread = new Thread(Loop) { IsBackground = true, Name = ThreadName };
                _thread.Start();
            }
        }

        private static void Loop()
        {
            foreach (var job in _jobs.GetConsumingEnumerable()) Run(job);
        }

        // Tagged through FiresLogger: BepInEx only hears Unity's untagged Debug.Log from the main thread, and Core
        // carries tagged lines from other threads over to it.
        private static void Run(Action job)
        {
            try { job(); }
            catch (Exception ex) { FiresLogger.LogWarning($"[ClientLogRelay] background job failed: {ex.Message}"); }
        }
    }
}

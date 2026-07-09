using System;
using System.Collections.Generic;
using System.Threading;
using UnityEditor;
using UnityEngine;

namespace Gamebrew.Bridge
{
    [InitializeOnLoad]
    public static class MainThreadDispatcher
    {
        private static readonly Queue<Action> _queue = new Queue<Action>();
        private static readonly List<ManualResetEventSlim> _pendingGates = new List<ManualResetEventSlim>();
        private static readonly object _lock = new object();
        private static readonly int _mainThreadId = Thread.CurrentThread.ManagedThreadId;
        private static bool _autoPumpFromUpdate = true;

        static MainThreadDispatcher()
        {
            Application.runInBackground = true;
            EditorApplication.update += OnEditorUpdate;
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
        }

        /// <summary>Disable EditorApplication.update pumping (EditMode timeout tests).</summary>
        public static void SetAutoPumpFromUpdate(bool enabled) => _autoPumpFromUpdate = enabled;

        private static void OnEditorUpdate()
        {
            if (_autoPumpFromUpdate) Pump();
        }

        public static void Enqueue(Action work)
        {
            if (work == null) throw new ArgumentNullException(nameof(work));
            lock (_lock)
            {
                _queue.Enqueue(work);
            }
            // QueuePlayerLoopUpdate is main-thread-only; HTTP callbacks enqueue from threadpool.
            if (Thread.CurrentThread.ManagedThreadId == _mainThreadId)
            {
                EditorApplication.QueuePlayerLoopUpdate();
            }
        }

        public static void Run(Action fn, int timeoutMs = 10000)
        {
            if (fn == null) throw new ArgumentNullException(nameof(fn));
            Run(() =>
            {
                fn();
                return true;
            }, timeoutMs);
        }

        public static T Run<T>(Func<T> fn, int timeoutMs = 10000)
        {
            if (fn == null) throw new ArgumentNullException(nameof(fn));

            if (Thread.CurrentThread.ManagedThreadId == _mainThreadId)
            {
                return fn();
            }

            T result = default;
            Exception captured = null;
            var gate = new ManualResetEventSlim(false);
            RegisterGate(gate);

            try
            {
                Enqueue(() =>
                {
                    try
                    {
                        result = fn();
                    }
                    catch (Exception ex)
                    {
                        captured = ex;
                    }
                    finally
                    {
                        gate.Set();
                    }
                });

                if (!gate.Wait(timeoutMs))
                {
                    throw new TimeoutException(
                        $"MainThreadDispatcher.Run timed out after {timeoutMs} ms waiting for main thread.");
                }

                if (captured != null)
                {
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(captured).Throw();
                }

                return result;
            }
            finally
            {
                UnregisterGate(gate);
            }
        }

        public static void ProcessPending() => Pump();

        private static void RegisterGate(ManualResetEventSlim gate)
        {
            lock (_lock)
            {
                _pendingGates.Add(gate);
            }
        }

        private static void UnregisterGate(ManualResetEventSlim gate)
        {
            lock (_lock)
            {
                _pendingGates.Remove(gate);
            }
        }

        private static void OnBeforeAssemblyReload()
        {
            lock (_lock)
            {
                _queue.Clear();
                foreach (var gate in _pendingGates)
                {
                    try { gate.Set(); } catch { /* domain teardown */ }
                }
                _pendingGates.Clear();
            }
        }

        private static void Pump()
        {
            while (true)
            {
                Action work;
                lock (_lock)
                {
                    if (_queue.Count == 0) return;
                    work = _queue.Dequeue();
                }
                try
                {
                    work();
                }
                catch (Exception ex)
                {
                    Debug.LogException(ex);
                }
            }
        }
    }
}

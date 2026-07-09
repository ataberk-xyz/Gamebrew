using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;

namespace Gamebrew.Bridge
{
    public readonly struct LogEntry
    {
        public readonly string Type;
        public readonly string Message;
        public readonly string StackTrace;

        public LogEntry(string type, string message, string stackTrace)
        {
            Type = type;
            Message = message;
            StackTrace = stackTrace;
        }
    }

    /// <summary>
    /// Thread-safe ring buffer of the last <see cref="Capacity"/> Unity log messages.
    /// </summary>
    [InitializeOnLoad]
    public static class ConsoleLogBuffer
    {
        public const int Capacity = 500;

        private static readonly LogEntry[] _ring = new LogEntry[Capacity];
        private static int _head;
        private static int _count;
        private static readonly object _lock = new object();

        static ConsoleLogBuffer()
        {
            Application.logMessageReceived += OnLog;
            Application.logMessageReceivedThreaded += OnLog;
        }

        private static void OnLog(string message, string stackTrace, LogType type)
        {
            Append(type.ToString().ToLowerInvariant(), message, stackTrace);
        }

        /// <summary>Direct append for tests (avoids Unity log pipeline / LogAssert noise).</summary>
        public static void RecordForTests(string type, string message, string stackTrace = "")
        {
            Append(type, message, stackTrace);
        }

        private static void Append(string type, string message, string stackTrace)
        {
            var entry = new LogEntry(type, message, stackTrace);
            lock (_lock)
            {
                _ring[_head] = entry;
                _head = (_head + 1) % Capacity;
                if (_count < Capacity) _count++;
            }
        }

        public static IReadOnlyList<LogEntry> Recent(int count)
        {
            lock (_lock)
            {
                if (count <= 0) return Array.Empty<LogEntry>();
                int take = Math.Min(count, _count);
                var result = new LogEntry[take];

                for (int i = 0; i < take; i++)
                {
                    int idx = ((_head - take + i) % Capacity + Capacity) % Capacity;
                    result[i] = _ring[idx];
                }
                return result;
            }
        }

        public static void Clear()
        {
            lock (_lock)
            {
                _head = 0;
                _count = 0;
                Array.Clear(_ring, 0, _ring.Length);
            }
        }
    }
}

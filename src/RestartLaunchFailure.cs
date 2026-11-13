using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Text;

namespace KellysJOINCHECK
{
    internal enum RestartLaunchFailureKind { Unknown, Cancelled, Blocked, TimedOut }

    // These errors describe a failed Windows launch, not which security product
    // caused it. Never turn an HRESULT or a timeout into a SmartScreen verdict.
    internal static class RestartLaunchFailure
    {
        internal const int MaximumErrors = 16;
        internal const int MaximumEvidenceChars = 2048;
        private const string ManualGuidance = "If Windows shows a security prompt, close it. Choose Restart manually, then reopen Nuclear Option through Steam.";

        internal static string Guidance(Exception? error)
        {
            string reason;
            switch (Classify(error))
            {
                case RestartLaunchFailureKind.Cancelled: reason = "Windows cancelled restart preparation. "; break;
                case RestartLaunchFailureKind.Blocked: reason = "Windows blocked restart preparation. "; break;
                case RestartLaunchFailureKind.TimedOut: reason = "Restart preparation did not finish in time. "; break;
                default: reason = "Automatic restart could not start. "; break;
            }
            return reason + ManualGuidance;
        }

        internal static RestartLaunchFailureKind Classify(Exception? error)
        {
            var result = RestartLaunchFailureKind.Unknown;
            foreach (Exception current in Errors(error))
            {
                int code = current is Win32Exception native ? native.NativeErrorCode : Win32Code(current.HResult);
                if (code == 5 || code == 1260 || code == 225 || code == 226) return RestartLaunchFailureKind.Blocked;
                if (code == 1223) result = RestartLaunchFailureKind.Cancelled;
                else if (current is TimeoutException && result == RestartLaunchFailureKind.Unknown) result = RestartLaunchFailureKind.TimedOut;
            }
            return result;
        }

        internal static string Evidence(Exception? error)
        {
            var evidence = new StringBuilder();
            const string omitted = "[remaining exception evidence omitted]";
            foreach (Exception current in Errors(error))
            {
                string item = TypeName(current) + " HRESULT 0x" + unchecked((uint)current.HResult).ToString("X8", CultureInfo.InvariantCulture);
                int code = current is Win32Exception native ? native.NativeErrorCode : Win32Code(current.HResult);
                if (current is Win32Exception || code != 0) item += ", Win32 " + code.ToString(CultureInfo.InvariantCulture);
                if (evidence.Length + item.Length + 3 + omitted.Length > MaximumEvidenceChars)
                {
                    if (evidence.Length > 0) evidence.Append(" -> ");
                    evidence.Append(omitted);
                    break;
                }
                if (evidence.Length > 0) evidence.Append(" -> ");
                evidence.Append(item);
            }
            return evidence.Length == 0 ? "No exception evidence." : evidence.ToString();
        }

        private static IEnumerable<Exception> Errors(Exception? error)
        {
            if (error == null) yield break;
            var pending = new Queue<Exception>();
            pending.Enqueue(error);
            int inspected = 0;
            while (pending.Count > 0 && inspected < MaximumErrors)
            {
                Exception current = pending.Dequeue();
                inspected++;
                yield return current;
                if (current is AggregateException aggregate)
                {
                    for (int i = 0; i < aggregate.InnerExceptions.Count && inspected + pending.Count < MaximumErrors; i++)
                        pending.Enqueue(aggregate.InnerExceptions[i]);
                }
                else if (current.InnerException != null && inspected + pending.Count < MaximumErrors)
                    pending.Enqueue(current.InnerException);
            }
        }

        private static string TypeName(Exception error)
        {
            string name = error.GetType().Name;
            var safe = new StringBuilder(Math.Min(96, name.Length));
            for (int i = 0; i < name.Length && safe.Length < 96; i++)
            {
                char c = name[i];
                safe.Append(c >= 'a' && c <= 'z' || c >= 'A' && c <= 'Z' || c >= '0' && c <= '9' || c == '_' || c == '.' || c == '`' || c == '+' ? c : '?');
            }
            return safe.ToString();
        }

        private static int Win32Code(int result)
        {
            uint value = unchecked((uint)result);
            return (value & 0xffff0000u) == 0x80070000u ? (int)(value & 0xffffu) : 0;
        }
    }
}

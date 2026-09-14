using System;
using System.Diagnostics;
using System.Threading.Tasks;
using UnityEditor;

namespace Lev.Git.Preview
{
    internal static class EditorWait
    {
        /// <summary>
        /// Ждёт условия, опрашивая его в цикле редактора: запросы Unity вроде
        /// UnityWebRequest продвигаются только там. true — дождались, false — вышло время.
        /// </summary>
        public static Task<bool> Until(Func<bool> done, int timeoutMs)
        {
            var tcs = new TaskCompletionSource<bool>();
            var watch = Stopwatch.StartNew();

            EditorApplication.CallbackFunction tick = null;
            tick = () =>
            {
                bool ready;
                try { ready = done(); }
                catch { ready = false; }

                if (!ready && watch.ElapsedMilliseconds < timeoutMs) return;
                EditorApplication.update -= tick;
                tcs.TrySetResult(ready);
            };

            EditorApplication.update += tick;
            return tcs.Task;
        }
    }
}

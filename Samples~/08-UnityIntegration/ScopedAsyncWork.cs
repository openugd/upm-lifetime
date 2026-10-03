using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace OpenUGD.Samples.UnityIntegration
{
    /// <summary>
    /// Async work bounded by a GameObject's scope: destroying the object cancels the work, and the task is
    /// never silently discarded.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two failure modes this addresses, both of them common in Unity code:
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// <b>Work that outlives its object.</b> An <c>async</c> method keeps running after <c>OnDestroy</c>
    /// and then touches a destroyed component. <c>AsCancellationToken</c> makes the object's scope the
    /// cancellation source, so destruction cancels the work.
    /// </description></item>
    /// <item><description>
    /// <b>The discarded task.</b> <c>_ = DoWorkAsync();</c> throws away the only handle to the operation,
    /// so a failure inside it vanishes without a log line. Keep the task and observe it — as below.
    /// </description></item>
    /// </list>
    /// <para>
    /// <b>Which thread resumes.</b> <c>Start</c> runs on the main thread, where Unity installs its
    /// <c>UnitySynchronizationContext</c>. An <c>await</c> captures it, so the code after
    /// <c>await Task.Delay(...)</c> is posted back to the main thread and runs there on a later frame:
    /// touching Unity objects after the <c>await</c> is fine. That stops being true after
    /// <c>ConfigureAwait(false)</c>, or in code started on another thread (<c>Task.Run</c>), which resumes on
    /// a thread-pool thread.
    /// </para>
    /// <para>
    /// Resuming on the main thread does not mean resuming only while the object exists: without the token
    /// the continuation is queued, and runs, after <c>OnDestroy</c> all the same. Cancelling it is what
    /// the token is for.
    /// </para>
    /// </remarks>
    [RequireComponent(typeof(LifetimeScope))]
    public class ScopedAsyncWork : MonoBehaviour
    {
        private Task _work;

        private void Start()
        {
            var scope = GetComponent<LifetimeScope>();

            // One token for the whole component. Do not call AsCancellationToken in Update or in a loop:
            // every call on a live lifetime allocates a source and adds an entry held until termination.
            CancellationToken token = scope.Lifetime.AsCancellationToken();

            _work = RunAsync(token);

            // Never `_ = RunAsync(token);` — that discards the task, and with it every error inside it.
            // Observe the task instead, so a failure is loud.
            _work.ContinueWith(
                task => Debug.LogException(task.Exception),
                TaskContinuationOptions.OnlyOnFaulted);
        }

        private static async Task RunAsync(CancellationToken token)
        {
            try
            {
                while (true)
                {
                    // Cancelled the moment the GameObject is destroyed, because that terminates the
                    // scope, which cancels the token.
                    await Task.Delay(TimeSpan.FromSeconds(1), token);

                    // Reached only while the scope is alive, and on the main thread: the await captured
                    // Unity's SynchronizationContext, so this continuation was posted back to it.
                    Debug.Log("tick");
                }
            }
            catch (OperationCanceledException)
            {
                Debug.Log("work cancelled: the GameObject's scope ended");
            }
        }
    }
}

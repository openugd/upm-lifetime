using System;
using System.Threading;
using System.Threading.Tasks;

namespace OpenUGD.Samples.Cancellation
{
    /// <summary>
    /// <c>lifetime.AsCancellationToken()</c> — the bridge from a scope to every async API in .NET.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A scope and a <see cref="CancellationToken"/> are the same idea seen from two sides, so the
    /// conversion is one method. Anything that takes a token — <c>Task.Delay</c>, <c>HttpClient</c>, your
    /// own <c>async</c> methods — becomes scope-aware for free, and you never write a second cancellation
    /// mechanism alongside your lifetimes.
    /// </para>
    /// <para>
    /// <b>Already-terminated lifetimes return an already-cancelled token</b> (allocating and registering
    /// nothing). <i>Before 2.0.0 this returned a token that could never cancel, so any <c>await</c> on it
    /// hung forever.</i> If the lifetime terminates <i>during</i> the call instead, the cancel is invoked
    /// immediately by <c>AddAction</c> — the race cannot produce a token that never cancels.
    /// </para>
    /// <para>
    /// <b>Cost.</b> Each call on a live lifetime allocates one <see cref="CancellationTokenSource"/> and
    /// adds one entry to the lifetime's action list, held until termination. Call it once and keep the
    /// token, or scope it to a nested definition. Never call it in a loop against a long-lived lifetime,
    /// least of all <see cref="Lifetime.Eternal"/>, which never terminates.
    /// </para>
    /// <para>
    /// <b>Ownership.</b> The source is deliberately never disposed — the token is handed out and the
    /// method cannot know who still holds it. It is not a leak: a <see cref="CancellationTokenSource"/>
    /// has no finaliser and holds no unmanaged resource unless someone materialises
    /// <c>CancellationToken.WaitHandle</c> or sets a timer, and this does neither.
    /// </para>
    /// </remarks>
    public static class CancellationSample
    {
        public static async Task RunAsync(Action<string> log)
        {
            if (log == null) throw new ArgumentNullException(nameof(log));

            // -------------------------------------------------------------------------------------------
            // 1. Ending the scope cancels the work.
            // -------------------------------------------------------------------------------------------
            log("-- scope termination cancels in-flight work --");
            var request = Lifetime.Define(Lifetime.Eternal, "request");

            // One call, one token, kept for the duration of the scope.
            CancellationToken token = request.Lifetime.AsCancellationToken();

            Task work = DownloadAsync(token, log);

            // Whoever owns the scope decides. The async method never learns about Lifetime at all — it
            // only ever sees a CancellationToken, which is exactly the .NET-idiomatic signature.
            request.Terminate();

            await work;

            // -------------------------------------------------------------------------------------------
            // 2. Work that finishes before the scope ends. Closing the scope afterwards is harmless.
            // -------------------------------------------------------------------------------------------
            log("-- work that completes normally --");
            using (var quick = Lifetime.Define(Lifetime.Eternal, "quick"))
            {
                await DownloadAsync(quick.Lifetime.AsCancellationToken(), log, TimeSpan.FromMilliseconds(10));
            }

            // -------------------------------------------------------------------------------------------
            // 3. A token taken from a scope that has ALREADY ended is already cancelled.
            //    This is the 2.0.0 fix that matters most here: awaiting it fails fast instead of hanging.
            // -------------------------------------------------------------------------------------------
            log("-- a token from a dead scope is born cancelled --");
            var dead = Lifetime.Define(Lifetime.Eternal, "dead");
            dead.Terminate();

            CancellationToken deadToken = dead.Lifetime.AsCancellationToken();
            log("  IsCancellationRequested: " + deadToken.IsCancellationRequested); // True

            await DownloadAsync(deadToken, log);

            // -------------------------------------------------------------------------------------------
            // 4. Scope the token itself when the operation is shorter than the owner.
            // -------------------------------------------------------------------------------------------
            log("-- per-operation tokens: nest, do not accumulate --");
            var screen = Lifetime.Define(Lifetime.Eternal, "screen");
            for (var i = 0; i < 3; i++)
            {
                // A nested definition per iteration. Terminating it drops both the token source and the
                // entry it added, so `screen` does not grow one registration per loop turn.
                using var attempt = screen.Lifetime.DefineNested("attempt-" + i);
                await DownloadAsync(attempt.Lifetime.AsCancellationToken(), log, TimeSpan.FromMilliseconds(5));
            }

            screen.Terminate();
        }

        /// <summary>
        /// An ordinary async method. It knows nothing about lifetimes — that is the point of the bridge.
        /// </summary>
        private static async Task DownloadAsync(
            CancellationToken token,
            Action<string> log,
            TimeSpan? duration = null)
        {
            try
            {
                log("  download: started");
                await Task.Delay(duration ?? TimeSpan.FromSeconds(30), token);
                log("  download: finished");
            }
            catch (OperationCanceledException)
            {
                // TaskCanceledException derives from OperationCanceledException, so this catches both.
                log("  download: cancelled because the scope ended");
            }
        }
    }
}

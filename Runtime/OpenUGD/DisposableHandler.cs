using System;
using System.Threading;

namespace OpenUGD
{
    /// <summary>
    /// Turns an <see cref="Action"/> into an <see cref="IDisposable"/> that runs it exactly once, however
    /// many times <see cref="Dispose"/> is called.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The adapter between the two clean-up vocabularies in this stack. <see cref="Lifetime"/> speaks in
    /// actions; the rest of .NET speaks in <see cref="IDisposable"/>. This converts one to the other so a
    /// lambda can be handed to a <c>using</c> statement, or to any API that takes a disposable.
    /// </para>
    /// <para>
    /// <b>Run-once is the point.</b> <see cref="LifetimeExtensions.With"/> calls <c>Dispose</c> once per
    /// registration, so registering one instance on two lifetimes disposes it twice — its own documentation
    /// says to use a type whose <c>Dispose</c> is idempotent, and this is that type. The guard is
    /// <see cref="Interlocked.Exchange{T}(ref T, T)"/>, so the action runs once even under concurrent
    /// disposal, matching the thread-safety guarantee the rest of this package makes.
    /// </para>
    /// <para>
    /// An exception thrown by the action propagates to the caller of <see cref="Dispose"/>. The action is
    /// cleared before it runs, so a throwing action is <i>not</i> retried by a later <c>Dispose</c> — it has
    /// had its one attempt. It is also released for collection, so the closure does not keep its captures
    /// alive after disposal.
    /// </para>
    /// </remarks>
    public sealed class DisposableHandler : IDisposable
    {
        private Action _onDispose;

        /// <summary>
        /// Creates a disposable that runs <paramref name="onDispose"/> on first disposal.
        /// </summary>
        /// <param name="onDispose">The clean-up to run. Must not be <c>null</c>.</param>
        /// <exception cref="ArgumentNullException"><paramref name="onDispose"/> is <c>null</c>.</exception>
        public DisposableHandler(Action onDispose)
        {
            if (onDispose == null)
            {
                throw new ArgumentNullException(nameof(onDispose), "Dispose action cannot be null.");
            }

            _onDispose = onDispose;
        }

        /// <summary>
        /// Runs the action if it has not run yet; otherwise does nothing.
        /// </summary>
        /// <remarks>Safe to call concurrently and repeatedly: exactly one caller runs the action.</remarks>
        public void Dispose()
        {
            var onDispose = Interlocked.Exchange(ref _onDispose, null);
            onDispose?.Invoke();
        }
    }
}

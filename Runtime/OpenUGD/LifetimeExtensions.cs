using System;
using System.Threading;

namespace OpenUGD
{
    public static class LifetimeExtensions
    {
        public static void With(this IDisposable disposable, Lifetime lifetime)
        {
            lifetime.AddAction(disposable.Dispose);
        }

        public static CancellationToken AsCancellationToken(this Lifetime lifetime)
        {
            var tokenSource = new CancellationTokenSource();
            lifetime.AddAction(tokenSource.Cancel);
            return tokenSource.Token;
        }
    }
}

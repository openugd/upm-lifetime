using System;

namespace OpenUGD
{
    public static class LifetimeExtensions
    {
        public static void With(this IDisposable disposable, Lifetime lifetime)
        {
            lifetime.AddAction(disposable.Dispose);
        }
    }
}

using System;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace OpenUGD.Samples.UnityIntegration.Tests
{
    /// <summary>
    /// EditMode checks of <see cref="LifetimeScope"/>. Unity does not run <c>Awake</c> or <c>OnDestroy</c> in Edit
    /// Mode, so only what happens without them is checked here.
    /// </summary>
    [TestFixture]
    [Category("RequiresUnity")]
    public class LifetimeScopeTests
    {
        private GameObject _gameObject;

        [TearDown]
        public void TearDown()
        {
            if (_gameObject != null) Object.DestroyImmediate(_gameObject);
        }

        [Test]
        public void Lifetime_OnAGameObjectThatHasNeverBeenActive_Throws()
        {
            // Unity never calls OnDestroy on an object that has never been active. A scope created for it here
            // would stay nested in Lifetime.Eternal for the rest of the process, so the read must fail instead.
            _gameObject = new GameObject("never-active");
            _gameObject.SetActive(false);
            var scope = _gameObject.AddComponent<LifetimeScope>();

            Assert.Throws<InvalidOperationException>(() => GC.KeepAlive(scope.Lifetime));
        }
    }
}

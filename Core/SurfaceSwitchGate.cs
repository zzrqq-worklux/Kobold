namespace Kobold.Core
{
    /// <summary>
    /// A tiny UI-thread epoch for one widget surface: every activation request
    /// takes a new token through <see cref="Begin"/>, and a deferred side
    /// effect runs only while its token is still current. A hide/close calls
    /// <see cref="Invalidate"/> so every request made before it goes stale.
    /// There is no async state and each switch allocates nothing.
    /// </summary>
    public sealed class SurfaceSwitchGate
    {
        private int _currentToken;

        /// <summary>The latest token, or 0 before the first <see cref="Begin"/>.</summary>
        public int CurrentToken => _currentToken;

        /// <summary>Makes a new, increasing token the latest one.</summary>
        public int Begin() => unchecked(++_currentToken);

        /// <summary>True only for the latest token; 0 is never current.</summary>
        public bool IsCurrent(int token) => token != 0 && token == _currentToken;

        /// <summary>
        /// Makes every token issued so far stale. Safe to call repeatedly,
        /// including when nothing is pending.
        /// </summary>
        public void Invalidate()
        {
            unchecked
            {
                _currentToken++;
            }
        }
    }
}

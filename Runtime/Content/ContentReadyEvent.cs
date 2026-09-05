namespace Cuvara.Netcode.Content
{
    /// <summary>
    /// Published when the content pipeline has finished fetching and parsing game
    /// content from the server. The loading screen waits for this before transitioning.
    /// </summary>
    public readonly struct ContentReadyEvent
    {
        /// <summary>Number of content items loaded.</summary>
        public readonly int ItemCount;

        /// <summary>Whether content was served from cache (HTTP 304) or fetched fresh.</summary>
        public readonly bool FromCache;

        public ContentReadyEvent(int itemCount, bool fromCache)
        {
            ItemCount = itemCount;
            FromCache = fromCache;
        }

        public override string ToString() =>
            $"{ItemCount} items loaded" + (FromCache ? " (cached)" : " (fresh)");
    }
}

using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using Shared.GameLogic.Content;
using UnityEngine;
using UnityEngine.Networking;

namespace Cuvara.Netcode.Content
{
    /// <summary>
    /// Fetches the game's content set from a game server and keeps it for the session.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Content lives on the server as files and is served over HTTP at <c>/content</c>
    /// (ADR-19), so a content change is a server restart rather than a client build. This
    /// is the client end of that: fetch once, cache by hash, and skip the body on every
    /// later fetch that finds the hash unchanged.
    /// </para>
    /// <para>
    /// <b>Caching is by hash, not by time.</b> There is no TTL, because content does not
    /// expire — it changes when a server restarts with different files, and the hash is how
    /// that is noticed. A time-based cache would either re-download content that had not
    /// changed or serve stale content that had.
    /// </para>
    /// </remarks>
    public sealed class ContentClient
    {
        const string CachedJsonKey = "content.json";
        const string CachedHashKey = "content.hash";

        /// <summary>The content in use, or <see cref="ContentDatabase.Empty"/> before a fetch.</summary>
        public ContentDatabase Database { get; private set; } = ContentDatabase.Empty;

        /// <summary>True once a fetch or a cache load has produced real content.</summary>
        public bool IsLoaded => Database.ItemCount > 0 || _loadedEmpty;

        /// <summary>How the current content was obtained. For diagnostics and the test scene.</summary>
        public ContentSource Source { get; private set; } = ContentSource.None;

        bool _loadedEmpty;

        /// <summary>
        /// Fetches content from <paramref name="baseUrl"/>, sending the cached hash so an
        /// unchanged set costs one round trip of headers and no body.
        /// </summary>
        /// <param name="baseUrl">
        /// The game server's metrics origin, e.g. <c>http://127.0.0.1:9100</c>. The
        /// <c>/content</c> path is appended.
        /// </param>
        /// <exception cref="ContentException">
        /// The request failed, or the response could not be parsed or validated. Fatal to
        /// the join: a client with no item definitions cannot render an inventory, and
        /// guessing would put wrong numbers in front of a player.
        /// </exception>
        public async UniTask FetchAsync(string baseUrl, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(baseUrl))
                throw new ContentException("Content base URL is empty.");

            string cachedHash = PlayerPrefs.GetString(CachedHashKey, string.Empty);
            string url = baseUrl.TrimEnd('/') + "/content";
            if (!string.IsNullOrEmpty(cachedHash))
            {
                url += "?hash=" + UnityWebRequest.EscapeURL(cachedHash);
            }

            using var request = UnityWebRequest.Get(url);
            try
            {
                await request.SendWebRequest().WithCancellation(ct);
            }
            catch (UnityWebRequestException ex)
            {
                // A 304 arrives here as an exception in some Unity versions rather than as a
                // result, because UnityWebRequest treats any non-2xx as a protocol error.
                // Handled rather than rethrown: 304 is the successful steady-state answer,
                // and treating it as a failure would make every launch after the first
                // report a content error while working perfectly.
                if (request.responseCode == 304)
                {
                    LoadFromCache(cachedHash);
                    return;
                }

                throw new ContentException(
                    $"Could not fetch content from {url}: {ex.Message}. The client has no item " +
                    "definitions and cannot render inventory or loot.", ex);
            }

            if (request.responseCode == 304)
            {
                LoadFromCache(cachedHash);
                return;
            }

            // Prefer the explicit header over ETag. UnityWebRequest and several proxies
            // rewrite or strip ETag, and a client that cannot read back the hash it was just
            // given can never send ?hash= — so every launch silently re-downloads while
            // appearing to work.
            string hash = request.GetResponseHeader("X-Content-Hash");
            if (string.IsNullOrEmpty(hash))
            {
                hash = (request.GetResponseHeader("ETag") ?? string.Empty).Trim('"');
            }

            if (string.IsNullOrEmpty(hash))
            {
                throw new ContentException(
                    $"{url} returned content with no hash header. Without one the client cannot " +
                    "cache it, and would re-download the whole set on every launch.");
            }

            string json = request.downloadHandler.text;
            if (!ContentJsonReader.TryRead(json, hash, out var database, out string error))
            {
                throw new ContentException($"Content from {url} is unusable: {error}");
            }

            Adopt(database, ContentSource.Network);

            PlayerPrefs.SetString(CachedJsonKey, json);
            PlayerPrefs.SetString(CachedHashKey, hash);
            PlayerPrefs.Save();
        }

        /// <summary>
        /// Loads content from a local string. For the test scene and for editor work
        /// without a running server.
        /// </summary>
        public void LoadFromJson(string json, string hash)
        {
            if (!ContentJsonReader.TryRead(json, hash, out var database, out string error))
            {
                throw new ContentException("Local content is unusable: " + error);
            }

            Adopt(database, ContentSource.Local);
        }

        void LoadFromCache(string hash)
        {
            string json = PlayerPrefs.GetString(CachedJsonKey, string.Empty);

            // The server said "unchanged" against a hash this client claimed to hold, so an
            // empty cache here means the two stores disagree — hash kept, body lost. Clearing
            // the hash makes the next attempt a full download instead of an unrecoverable
            // 304 loop.
            if (string.IsNullOrEmpty(json))
            {
                PlayerPrefs.DeleteKey(CachedHashKey);
                PlayerPrefs.Save();
                throw new ContentException(
                    "Server reported content unchanged, but the local cache is empty. The cached " +
                    "hash has been cleared; the next fetch will download the full set.");
            }

            if (!ContentJsonReader.TryRead(json, hash, out var database, out string error))
            {
                PlayerPrefs.DeleteKey(CachedHashKey);
                PlayerPrefs.DeleteKey(CachedJsonKey);
                PlayerPrefs.Save();
                throw new ContentException(
                    $"Cached content is unusable ({error}). The cache has been cleared; the next " +
                    "fetch will download the full set.");
            }

            Adopt(database, ContentSource.Cache);
        }

        void Adopt(ContentDatabase database, ContentSource source)
        {
            Database = database;
            Source = source;
            _loadedEmpty = true;
            Debug.Log($"[Content] {database.ItemCount} items, hash {database.Hash} ({source})");
        }
    }

    /// <summary>Where the content in use came from.</summary>
    public enum ContentSource
    {
        None = 0,
        Network,
        Cache,
        Local,
    }

    /// <summary>Content could not be fetched, parsed or validated.</summary>
    public sealed class ContentException : Exception
    {
        public ContentException(string message) : base(message) { }
        public ContentException(string message, Exception inner) : base(message, inner) { }
    }
}

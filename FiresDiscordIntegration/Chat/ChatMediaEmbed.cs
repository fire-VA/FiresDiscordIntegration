using System.Collections;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UI;
using TMPro;

namespace FiresDiscordIntegration.ChatRelay
{
    /// <summary>
    /// MonoBehaviour attached to each embedded image/GIF in the chat media
    /// overlay.  Downloads the media, decodes GIF frames if animated, and
    /// displays the result as a <see cref="RawImage"/>.  Handles frame-by-frame
    /// animation and self-cleanup after a fixed lifetime.
    ///
    /// The parent overlay container is always active (it lives under
    /// Hud / IngameGui, not inside the chat window), so coroutines and
    /// Update() are never blocked by the chat being closed.
    /// </summary>
    public class ChatMediaEmbed : MonoBehaviour
    {
        private const float MaxEmbedWidth = 697f;  // ContainerWidth (713) minus padding
        private const float MaxEmbedHeight = 380f;  // ~half container height for comfortable stacking
        private const float LifetimeSeconds = 30f;
        // Client-side HTTP download — no Steam networking involved.
        private const int MaxDownloadBytes = 50 * 1024 * 1024;

        private RawImage _image;
        private Texture2D[] _gifFrames;
        private float[] _frameDelays;
        private int _currentFrame;
        private float _frameTimer;
        private bool _isAnimating;
        private float _spawnTime;

        // "Loading GIF…" placeholder
        private GameObject _loadingLabel;

        /// <summary>
        /// Initialize and start downloading from a URL.
        /// </summary>
        public void Load(string url, ChatMediaDetector.MediaType mediaType)
        {
            _spawnTime = Time.unscaledTime;

            // Create the RawImage component (invisible until image loads)
            _image = gameObject.AddComponent<RawImage>();
            _image.color = new Color(1f, 1f, 1f, 0f);

            // Show a "Loading GIF…" placeholder while downloading
            ShowLoadingLabel(mediaType);

            StartCoroutine(DownloadAndDisplay(url, mediaType));
        }

        // ─────────────────────────────────────────────────────────
        // Download & decode
        // ─────────────────────────────────────────────────────────

        private IEnumerator DownloadAndDisplay(string url, ChatMediaDetector.MediaType mediaType)
        {
            // ── Resolve page/share URLs to direct media URLs ──
            string resolvedUrl = null;
            yield return ResolveMediaUrl(url, result => resolvedUrl = result);
            if (!string.IsNullOrEmpty(resolvedUrl))
                url = resolvedUrl;

            Debug.Log($"[ChatMedia] Downloading: {url}");

            // ── First download attempt ──
            byte[] data = null;
            bool success = false;

            using (var request = UnityWebRequest.Get(url))
            {
                request.timeout = 30;
                request.SetRequestHeader("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) VerdantsAscent/1.0");

                yield return request.SendWebRequest();

                if (request.result == UnityWebRequest.Result.Success)
                {
                    data = request.downloadHandler.data;
                    success = data != null && data.Length > 0 && data.Length <= MaxDownloadBytes;
                }
                else
                {
                    Debug.LogWarning($"[ChatMedia] First attempt failed ({request.responseCode}): {request.error} — URL: {url}");
                }
            }

            // ── Retry with fallback URL on failure ──
            if (!success)
            {
                string fallbackUrl = BuildFallbackUrl(url);
                if (!string.IsNullOrEmpty(fallbackUrl) && fallbackUrl != url)
                {
                    Debug.Log($"[ChatMedia] Retrying with fallback: {fallbackUrl}");
                    using (var retry = UnityWebRequest.Get(fallbackUrl))
                    {
                        retry.timeout = 15;
                        retry.SetRequestHeader("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) VerdantsAscent/1.0");

                        yield return retry.SendWebRequest();

                        if (retry.result == UnityWebRequest.Result.Success)
                        {
                            data = retry.downloadHandler.data;
                            success = data != null && data.Length > 0 && data.Length <= MaxDownloadBytes;
                            if (success)
                                Debug.Log($"[ChatMedia] Fallback succeeded: {fallbackUrl}");
                        }
                        else
                        {
                            Debug.LogWarning($"[ChatMedia] Fallback also failed ({retry.responseCode}): {retry.error}");
                        }
                    }
                }
            }

            DestroyLoadingLabel();

            if (!success || data == null)
            {
                Destroy(gameObject);
                yield break;
            }

            // ── Decode ──
            if (mediaType == ChatMediaDetector.MediaType.AnimatedGif && IsGifData(data))
            {
                var result = GifDecoder.Decode(data);
                if (result.HasValue && result.Value.Frames.Length > 1)
                {
                    _gifFrames = result.Value.Frames;
                    _frameDelays = result.Value.Delays;
                    _currentFrame = 0;
                    _frameTimer = 0f;
                    _isAnimating = true;

                    ApplyTexture(_gifFrames[0], result.Value.Width, result.Value.Height);
                }
                else if (result.HasValue && result.Value.Frames.Length == 1)
                {
                    ApplyTexture(result.Value.Frames[0], result.Value.Width, result.Value.Height);
                }
                else
                {
                    LoadAsStaticImage(data);
                }
            }
            else
            {
                // If we expected a GIF but got non-GIF data, still try to display it
                // (some providers return PNG/JPG for preview thumbnails)
                LoadAsStaticImage(data);
            }
        }

        private void LoadAsStaticImage(byte[] data)
        {
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (LoadImageSafe(tex, data))
            {
                ApplyTexture(tex, tex.width, tex.height);
            }
            else
            {
                Object.Destroy(tex);
                Destroy(gameObject);
            }
        }

        /// <summary>
        /// Load image data into a Texture2D using reflection to avoid
        /// a hard reference to UnityEngine.ImageConversionModule.
        /// </summary>
        private static bool LoadImageSafe(Texture2D tex, byte[] data)
        {
            try
            {
                var imageConversionType = System.Type.GetType(
                    "UnityEngine.ImageConversion, UnityEngine.ImageConversionModule");
                if (imageConversionType != null)
                {
                    var method = imageConversionType.GetMethod("LoadImage",
                        new[] { typeof(Texture2D), typeof(byte[]), typeof(bool) });
                    if (method != null)
                        return (bool)method.Invoke(null, new object[] { tex, data, false });
                }

                var directMethod = typeof(Texture2D).GetMethod("LoadImage",
                    new[] { typeof(byte[]) });
                if (directMethod != null)
                    return (bool)directMethod.Invoke(tex, new object[] { data });
            }
            catch { }

            return false;
        }

        // ─────────────────────────────────────────────────────────
        // Texture / sizing
        // ─────────────────────────────────────────────────────────

        private void ApplyTexture(Texture2D tex, int srcWidth, int srcHeight)
        {
            if (_image == null) return;

            _image.texture = tex;
            _image.color = Color.white;

            // Fit within max bounds, preserving aspect ratio
            float aspect = (float)srcWidth / Mathf.Max(srcHeight, 1);
            float w = Mathf.Min(srcWidth, MaxEmbedWidth);
            float h = w / aspect;

            if (h > MaxEmbedHeight)
            {
                h = MaxEmbedHeight;
                w = h * aspect;
            }

            var rt = GetComponent<RectTransform>();
            if (rt != null)
                rt.sizeDelta = new Vector2(w, h);

            // Update the LayoutElement so the VerticalLayoutGroup re-flows
            var le = GetComponent<LayoutElement>();
            if (le != null)
            {
                le.preferredWidth = w;
                le.preferredHeight = h;
            }

            // Rebuild the parent layout
            if (rt != null && rt.parent != null)
            {
                var parentRt = rt.parent as RectTransform;
                if (parentRt != null)
                    LayoutRebuilder.ForceRebuildLayoutImmediate(parentRt);
            }
        }

        // ─────────────────────────────────────────────────────────
        // Animation
        // ─────────────────────────────────────────────────────────

        private void Update()
        {
            // Animate GIF frames
            if (_isAnimating && _gifFrames != null && _gifFrames.Length > 1)
            {
                _frameTimer += Time.unscaledDeltaTime;
                float delay = _currentFrame < _frameDelays.Length
                    ? _frameDelays[_currentFrame]
                    : 0.1f;

                if (_frameTimer >= delay)
                {
                    _frameTimer -= delay;
                    _currentFrame = (_currentFrame + 1) % _gifFrames.Length;

                    if (_image != null && _gifFrames[_currentFrame] != null)
                        _image.texture = _gifFrames[_currentFrame];
                }
            }

            // Auto-destroy after lifetime
            if (Time.unscaledTime - _spawnTime > LifetimeSeconds)
            {
                Cleanup();
            }
        }

        // ─────────────────────────────────────────────────────────
        // Loading placeholder
        // ─────────────────────────────────────────────────────────

        private void ShowLoadingLabel(ChatMediaDetector.MediaType mediaType)
        {
            string label = mediaType == ChatMediaDetector.MediaType.AnimatedGif
                ? "Loading GIF\u2026"
                : "Loading image\u2026";

            _loadingLabel = new GameObject("LoadingLabel");
            _loadingLabel.transform.SetParent(transform, false);

            var rt = _loadingLabel.AddComponent<RectTransform>();
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.sizeDelta = Vector2.zero;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;

            var tmp = _loadingLabel.AddComponent<TextMeshProUGUI>();
            tmp.text = label;
            tmp.fontSize = 16f;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.color = new Color(0.8f, 0.8f, 0.8f, 0.7f);
            tmp.textWrappingMode = TextWrappingModes.NoWrap;
            tmp.overflowMode = TextOverflowModes.Ellipsis;
            tmp.raycastTarget = false;
        }

        private void DestroyLoadingLabel()
        {
            if (_loadingLabel != null)
            {
                Destroy(_loadingLabel);
                _loadingLabel = null;
            }
        }

        // ─────────────────────────────────────────────────────────
        // Cleanup
        // ─────────────────────────────────────────────────────────

        /// <summary>
        /// Destroy all textures and remove the GameObject.
        /// </summary>
        public void Cleanup()
        {
            _isAnimating = false;

            if (_gifFrames != null)
            {
                foreach (var frame in _gifFrames)
                {
                    if (frame != null) Destroy(frame);
                }
                _gifFrames = null;
            }

            if (_image != null && _image.texture != null && _gifFrames == null)
            {
                Destroy(_image.texture);
            }

            DestroyLoadingLabel();
            Destroy(gameObject);
        }

        private void OnDestroy()
        {
            if (_gifFrames != null)
            {
                foreach (var frame in _gifFrames)
                {
                    if (frame != null) Destroy(frame);
                }
                _gifFrames = null;
            }
        }

        // ─────────────────────────────────────────────────────────
        // Helpers
        // ─────────────────────────────────────────────────────────

        private static bool IsGifData(byte[] data)
        {
            return data.Length >= 3 && data[0] == 'G' && data[1] == 'I' && data[2] == 'F';
        }

        /// <summary>
        /// Resolve share/page URLs from Tenor, Giphy, Klipy etc. to direct
        /// media URLs.  Returns null if no resolution is needed (URL is
        /// already a direct media link).
        /// </summary>
        private IEnumerator ResolveMediaUrl(string url, System.Action<string> callback)
        {
            // ── Tenor page URL → direct GIF via oEmbed ──
            if (url.Contains("tenor.com/view/"))
            {
                yield return ResolveTenorOEmbed(url, callback);
                yield break;
            }

            // ── Giphy page URL → direct media ──
            // https://giphy.com/gifs/SLUG-ID → https://media.giphy.com/media/ID/giphy.gif
            if (url.Contains("giphy.com/gifs/"))
            {
                string giphyId = ExtractGiphyId(url);
                if (!string.IsNullOrEmpty(giphyId))
                {
                    callback($"https://media.giphy.com/media/{giphyId}/giphy.gif");
                    yield break;
                }
            }

            // ── i.giphy.com short link → direct media ──
            // https://i.giphy.com/media/ID/giphy.gif (already direct)
            // https://i.giphy.com/ID.gif
            if (url.Contains("i.giphy.com") && !url.EndsWith(".gif", System.StringComparison.OrdinalIgnoreCase))
            {
                string giphyId = ExtractLastPathSegment(url);
                if (!string.IsNullOrEmpty(giphyId))
                {
                    callback($"https://i.giphy.com/media/{giphyId}/giphy.gif");
                    yield break;
                }
            }

            // ── Klipy page URL → attempt direct CDN ──
            // https://klipy.com/gifs/SLUG → try https://klipy.com/gifs/SLUG.gif
            if (url.Contains("klipy.com/gifs/") && !url.EndsWith(".gif", System.StringComparison.OrdinalIgnoreCase))
            {
                callback(url.TrimEnd('/') + ".gif");
                yield break;
            }

            // No resolution needed — URL is already a direct link
            callback(null);
        }

        /// <summary>
        /// Resolve Tenor page URL via oEmbed API.  Tries multiple fields
        /// in priority order: url (embed page), thumbnail_url, and falls
        /// back to constructing a media.tenor.com link from the GIF ID.
        /// </summary>
        private IEnumerator ResolveTenorOEmbed(string tenorUrl, System.Action<string> callback)
        {
            string oembedUrl = $"https://tenor.com/oembed?url={UnityWebRequest.EscapeURL(tenorUrl)}";

            using (var request = UnityWebRequest.Get(oembedUrl))
            {
                request.timeout = 5;
                request.SetRequestHeader("User-Agent", "Mozilla/5.0 VerdantsAscent/1.0");

                yield return request.SendWebRequest();

                if (request.responseCode == 200 && request.downloadHandler != null)
                {
                    try
                    {
                        string body = request.downloadHandler.text;

                        // Try to find a direct .gif or .mp4 URL in the response.
                        // Tenor oEmbed can include "url" (media page), "thumbnail_url",
                        // or an <img> tag in "html" with the actual GIF src.
                        // Priority: src from html > thumbnail_url ending in .gif > thumbnail_url

                        // 1) Extract src="..." from the html field — most reliable
                        var htmlSrcMatch = System.Text.RegularExpressions.Regex.Match(body,
                            @"src=""([^""]+\.gif[^""]*)"" ",
                            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                        if (htmlSrcMatch.Success)
                        {
                            string src = htmlSrcMatch.Groups[1].Value.Replace("\\u002F", "/").Replace("\\/", "/");
                            Debug.Log($"[ChatMedia] Tenor oEmbed html src: {src}");
                            callback(src);
                            yield break;
                        }

                        // 2) thumbnail_url — Tenor's oEmbed thumbnail is a PNG poster
                        //    frame, not the animated GIF.  Upgrade the URL by swapping
                        //    the CDN size-suffix and extension before handing it back,
                        //    so the embed downloader fetches the actual gif and our
                        //    decoder produces multiple frames instead of one.
                        var thumbMatch = System.Text.RegularExpressions.Regex.Match(body,
                            @"""thumbnail_url""\s*:\s*""([^""]+)""");
                        if (thumbMatch.Success)
                        {
                            string thumb = thumbMatch.Groups[1].Value.Replace("\\u002F", "/").Replace("\\/", "/");
                            string upgraded = UpgradeTenorPosterToGif(thumb);
                            if (upgraded != thumb)
                                Debug.Log($"[ChatMedia] Tenor oEmbed thumbnail upgraded to gif: {upgraded}");
                            else
                                Debug.Log($"[ChatMedia] Tenor oEmbed thumbnail: {thumb}");
                            callback(upgraded);
                            yield break;
                        }
                    }
                    catch (System.Exception ex)
                    {
                        Debug.LogWarning($"[ChatMedia] Tenor oEmbed parse error: {ex.Message}");
                    }
                }
                else
                {
                    Debug.LogWarning($"[ChatMedia] Tenor oEmbed failed ({request.responseCode}): {request.error}");
                }
            }

            // 3) Fallback: try to extract GIF ID from the page URL and build a
            //    media.tenor.com link.  Tenor URLs look like:
            //    https://tenor.com/view/slug-words-GIFID
            //    The numeric ID is the last segment after the last hyphen.
            string gifId = ExtractTenorGifId(tenorUrl);
            if (!string.IsNullOrEmpty(gifId))
            {
                string directUrl = $"https://media.tenor.com/images/{gifId}/tenor.gif";
                Debug.Log($"[ChatMedia] Tenor fallback from ID: {directUrl}");
                callback(directUrl);
                yield break;
            }

            // Nothing worked — return null (caller will use original URL)
            callback(null);
        }

        /// <summary>
        /// Build an alternative URL to try when the first download 404s.
        /// </summary>
        private static string BuildFallbackUrl(string failedUrl)
        {
            // Tenor: if a media.tenor.com link 404'd, try the /tenor.gif variant
            if (failedUrl.Contains("media.tenor.com"))
            {
                // If it doesn't end with a known extension, append /tenor.gif
                if (!failedUrl.EndsWith(".gif", System.StringComparison.OrdinalIgnoreCase) &&
                    !failedUrl.EndsWith(".png", System.StringComparison.OrdinalIgnoreCase) &&
                    !failedUrl.EndsWith(".mp4", System.StringComparison.OrdinalIgnoreCase))
                {
                    return failedUrl.TrimEnd('/') + "/tenor.gif";
                }
            }

            // Klipy: try without the .gif extension
            if (failedUrl.Contains("klipy.com") && failedUrl.EndsWith(".gif", System.StringComparison.OrdinalIgnoreCase))
            {
                return failedUrl.Substring(0, failedUrl.Length - 4);
            }

            return null;
        }

        // ─────────────────────────────────────────────────────────
        // URL parsing helpers
        // ─────────────────────────────────────────────────────────

        // Tenor CDN URLs encode the file format + size in the final 5 characters
        // of the asset ID, just before the slash that introduces the filename:
        //   media.tenor.com/{base11}AAAA{c}/file.{ext}
        // where {c} is a single character that picks the variant:
        //   C / d / i  → animated GIF, different sizes
        //   M / N / O / P → static PNG poster frames at different sizes
        //   e / 1      → MP4 (silent video)
        // The oEmbed thumbnail handed back to us picks one of the static PNG
        // variants (or a Discord proxy of the MP4 thumbnail), neither of which
        // animates.  Swap the suffix character to 'C' (the canonical animated
        // GIF variant) and the extension to .gif so we download something the
        // GifDecoder can actually decode into multiple frames.  Non-Tenor
        // URLs and Tenor URLs that don't match the pattern pass through
        // unchanged.
        private const char TenorAnimatedGifSuffixChar = 'C';
        private static readonly System.Text.RegularExpressions.Regex TenorPosterUrlPattern =
            new System.Text.RegularExpressions.Regex(
                @"^(?<prefix>https?://media\.tenor\.com/[A-Za-z0-9_-]+AAAA)" +
                @"[A-Za-z0-9]" +
                @"(?<tail>/[^/?]+)\.(?:png|jpe?g|webp)" +
                @"(?<query>\?.*)?$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase |
                System.Text.RegularExpressions.RegexOptions.Compiled);

        private static string UpgradeTenorPosterToGif(string url)
        {
            if (string.IsNullOrEmpty(url)) return url;
            var match = TenorPosterUrlPattern.Match(url);
            if (!match.Success) return url;
            return match.Groups["prefix"].Value
                 + TenorAnimatedGifSuffixChar
                 + match.Groups["tail"].Value
                 + ".gif"
                 + match.Groups["query"].Value;
        }

        /// <summary>
        /// Extract the numeric GIF ID from a Tenor page URL.
        /// Example: https://tenor.com/view/hello-wave-gif-12345 → "12345"
        /// </summary>
        private static string ExtractTenorGifId(string url)
        {
            try
            {
                // Strip query string and trailing slash
                int qIdx = url.IndexOf('?');
                if (qIdx >= 0) url = url.Substring(0, qIdx);
                url = url.TrimEnd('/');

                // The GIF ID is the last hyphen-separated segment if it's numeric
                int lastHyphen = url.LastIndexOf('-');
                if (lastHyphen >= 0 && lastHyphen < url.Length - 1)
                {
                    string candidate = url.Substring(lastHyphen + 1);
                    if (long.TryParse(candidate, out _))
                        return candidate;
                }

                // Also try the last path segment entirely
                int lastSlash = url.LastIndexOf('/');
                if (lastSlash >= 0 && lastSlash < url.Length - 1)
                {
                    string segment = url.Substring(lastSlash + 1);
                    lastHyphen = segment.LastIndexOf('-');
                    if (lastHyphen >= 0)
                    {
                        string candidate = segment.Substring(lastHyphen + 1);
                        if (long.TryParse(candidate, out _))
                            return candidate;
                    }
                }
            }
            catch { }
            return null;
        }

        /// <summary>
        /// Extract the Giphy media ID from a giphy.com/gifs/ URL.
        /// Example: https://giphy.com/gifs/reaction-slug-xT9ABCDEF → "xT9ABCDEF"
        /// The ID is the last hyphen-separated segment, or the entire last path segment.
        /// </summary>
        private static string ExtractGiphyId(string url)
        {
            try
            {
                int qIdx = url.IndexOf('?');
                if (qIdx >= 0) url = url.Substring(0, qIdx);
                url = url.TrimEnd('/');

                int lastSlash = url.LastIndexOf('/');
                if (lastSlash < 0) return null;
                string segment = url.Substring(lastSlash + 1);

                // Giphy IDs: last hyphen-separated part, or the whole segment
                int lastHyphen = segment.LastIndexOf('-');
                return lastHyphen >= 0 ? segment.Substring(lastHyphen + 1) : segment;
            }
            catch { }
            return null;
        }

        /// <summary>
        /// Extract the last path segment from a URL.
        /// </summary>
        private static string ExtractLastPathSegment(string url)
        {
            try
            {
                int qIdx = url.IndexOf('?');
                if (qIdx >= 0) url = url.Substring(0, qIdx);
                url = url.TrimEnd('/');
                int lastSlash = url.LastIndexOf('/');
                return lastSlash >= 0 ? url.Substring(lastSlash + 1) : null;
            }
            catch { }
            return null;
        }
    }
}


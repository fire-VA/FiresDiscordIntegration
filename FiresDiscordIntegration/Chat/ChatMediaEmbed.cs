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
        // Request timeouts (seconds): the media itself, the fallback URL, and the quick oEmbed lookup.
        private const int MediaDownloadTimeoutSeconds = 30;
        private const int FallbackDownloadTimeoutSeconds = 15;
        private const int OEmbedTimeoutSeconds = 5;

        private const float MaxEmbedWidth = 697f;  // ContainerWidth (713) minus padding
        private const float MaxEmbedHeight = 380f;  // ~half container height for comfortable stacking
        private const float LifetimeSeconds = 30f;
        // Client-side HTTP download — no Steam networking involved.
        private const int MaxDownloadBytes = 8 * 1024 * 1024;   // Security: the download aborts past this while reading

        private static readonly string[] AllowedMediaHosts = { "cdn.discordapp.com", "media.discordapp.net", "media.tenor.com", "media.giphy.com", "i.giphy.com" };

        internal static bool IsAllowedMediaUrl(string url)
        {
            if (string.IsNullOrEmpty(url) || url.Length > 2048) return false;
            if (!System.Uri.TryCreate(url, System.UriKind.Absolute, out var uri)) return false;
            if (uri.Scheme != System.Uri.UriSchemeHttps || !uri.IsDefaultPort || !string.IsNullOrEmpty(uri.UserInfo)) return false;
            for (int i = 0; i < AllowedMediaHosts.Length; i++)
                if (string.Equals(uri.Host, AllowedMediaHosts[i], System.StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static string SafeHost(string url) =>
            System.Uri.TryCreate(url ?? "", System.UriKind.Absolute, out var u) ? u.Host : "(invalid url)";

        // Collects the body and aborts the request once it passes the cap (and up front when Content-Length says so).
        private sealed class CappedDownloadHandler : DownloadHandlerScript
        {
            private readonly int _cap;
            private readonly System.IO.MemoryStream _ms = new System.IO.MemoryStream();
            public CappedDownloadHandler(int cap) : base(new byte[64 * 1024]) { _cap = cap; }
            protected override bool ReceiveData(byte[] chunk, int length)
            {
                if (chunk == null || length <= 0) return true;
                if (_ms.Length + length > _cap)
                {
                    Debug.LogWarning($"[ChatMedia] Download aborted: over {_cap} bytes.");
                    return false;
                }
                _ms.Write(chunk, 0, length);
                return true;
            }
            protected override void ReceiveContentLengthHeader(ulong contentLength)
            {
                if (contentLength > (ulong)_cap) Debug.LogWarning($"[ChatMedia] Download refused: Content-Length {contentLength} is over {_cap} bytes.");
            }
            protected override byte[] GetData() => _ms.ToArray();
        }

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
            // ── Resolve page/share URLs to direct media URLs (no network: Tenor/Giphy pages map to their media hosts) ──
            string resolvedUrl = null;
            yield return ResolveMediaUrl(url, result => resolvedUrl = result);
            if (!string.IsNullOrEmpty(resolvedUrl))
                url = resolvedUrl;

            // Security: only https media on the allow-listed hosts is fetched (no IP leak to arbitrary hosts, no LAN requests).
            if (!IsAllowedMediaUrl(url))
            {
                FiresLogger.LogDebug($"[ChatMedia] Not fetched (host not allowed): {SafeHost(url)}");
                DestroyLoadingLabel();
                Destroy(gameObject);
                yield break;
            }

            Debug.Log($"[ChatMedia] Downloading: {url}");

            byte[] data = null;
            yield return FetchAllowed(url, MediaDownloadTimeoutSeconds, d => data = d);
            bool success = data != null && data.Length > 0;

            // ── Retry with fallback URL on failure ──
            if (!success)
            {
                string fallbackUrl = BuildFallbackUrl(url);
                if (!string.IsNullOrEmpty(fallbackUrl) && fallbackUrl != url && IsAllowedMediaUrl(fallbackUrl))
                {
                    Debug.Log($"[ChatMedia] Retrying with fallback: {fallbackUrl}");
                    yield return FetchAllowed(fallbackUrl, FallbackDownloadTimeoutSeconds, d => data = d);
                    success = data != null && data.Length > 0;
                    if (success) Debug.Log($"[ChatMedia] Fallback succeeded: {fallbackUrl}");
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

        // Security: decoded-size caps for plain (non-GIF) images.
        private const int MaxStaticImageSide = 4096;
        private const long MaxStaticImagePixels = 16L * 1024 * 1024;
        private static bool s_warnedHeaderTooLarge;
        private static bool s_warnedDecodedTooLarge;

        private static bool IsWithinStaticCaps(long w, long h) =>
            w > 0 && h > 0 && w <= MaxStaticImageSide && h <= MaxStaticImageSide && w * h <= MaxStaticImagePixels;

        // Reads width/height from a PNG, JPEG or WebP header without decoding. False when the header is not recognised.
        internal static bool TryReadImageHeaderSize(byte[] d, out int width, out int height)
        {
            width = 0; height = 0;
            if (d == null) return false;
            try
            {
                // PNG: signature, then IHDR width/height (big-endian) at 16..23.
                if (d.Length >= 24 && d[0] == 0x89 && d[1] == 0x50 && d[2] == 0x4E && d[3] == 0x47
                    && d[12] == (byte)'I' && d[13] == (byte)'H' && d[14] == (byte)'D' && d[15] == (byte)'R')
                {
                    long pw = ((long)d[16] << 24) | ((long)d[17] << 16) | ((long)d[18] << 8) | d[19];
                    long ph = ((long)d[20] << 24) | ((long)d[21] << 16) | ((long)d[22] << 8) | d[23];
                    width = (int)System.Math.Min(pw, int.MaxValue); height = (int)System.Math.Min(ph, int.MaxValue);
                    return true;
                }
                // JPEG: walk the markers to the first SOFn.
                if (d.Length >= 4 && d[0] == 0xFF && d[1] == 0xD8)
                {
                    int i = 2;
                    while (i + 3 < d.Length)
                    {
                        if (d[i] != 0xFF) return false;
                        byte m = d[i + 1];
                        if (m == 0xFF) { i++; continue; }
                        if (m == 0xD8 || m == 0x01 || (m >= 0xD0 && m <= 0xD7)) { i += 2; continue; }
                        if (m == 0xD9 || m == 0xDA) return false;
                        int len = (d[i + 2] << 8) | d[i + 3];
                        if (len < 2) return false;
                        bool sof = m >= 0xC0 && m <= 0xCF && m != 0xC4 && m != 0xC8 && m != 0xCC;
                        if (sof)
                        {
                            if (i + 8 >= d.Length) return false;
                            height = (d[i + 5] << 8) | d[i + 6];
                            width = (d[i + 7] << 8) | d[i + 8];
                            return true;
                        }
                        i += 2 + len;
                    }
                    return false;
                }
                // WebP: RIFF....WEBP then VP8 / VP8L / VP8X.
                if (d.Length >= 30 && d[0] == 'R' && d[1] == 'I' && d[2] == 'F' && d[3] == 'F'
                    && d[8] == 'W' && d[9] == 'E' && d[10] == 'B' && d[11] == 'P' && d[12] == 'V' && d[13] == 'P' && d[14] == '8')
                {
                    byte k = d[15];
                    if (k == ' ')
                    {
                        width = (d[26] | (d[27] << 8)) & 0x3FFF;
                        height = (d[28] | (d[29] << 8)) & 0x3FFF;
                        return true;
                    }
                    if (k == 'L')
                    {
                        uint b = (uint)(d[21] | (d[22] << 8) | (d[23] << 16) | (d[24] << 24));
                        width = (int)(b & 0x3FFF) + 1;
                        height = (int)((b >> 14) & 0x3FFF) + 1;
                        return true;
                    }
                    if (k == 'X')
                    {
                        width = (d[24] | (d[25] << 8) | (d[26] << 16)) + 1;
                        height = (d[27] | (d[28] << 8) | (d[29] << 16)) + 1;
                        return true;
                    }
                }
            }
            catch { }
            return false;
        }

        private void LoadAsStaticImage(byte[] data)
        {
            // Security: refuse oversized images from the header before decoding.
            if (TryReadImageHeaderSize(data, out int hw, out int hh) && !IsWithinStaticCaps(hw, hh))
            {
                if (!s_warnedHeaderTooLarge)
                {
                    s_warnedHeaderTooLarge = true;
                    Debug.LogWarning($"[ChatMedia] Refused image: header says {hw}x{hh} (cap {MaxStaticImageSide} px per side, {MaxStaticImagePixels} pixels).");
                }
                Destroy(gameObject);
                return;
            }

            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (LoadImageSafe(tex, data))
            {
                // Security: check again after decoding (covers headers we could not read).
                if (!IsWithinStaticCaps(tex.width, tex.height))
                {
                    if (!s_warnedDecodedTooLarge)
                    {
                        s_warnedDecodedTooLarge = true;
                        Debug.LogWarning($"[ChatMedia] Refused image: decoded {tex.width}x{tex.height} (cap {MaxStaticImageSide} px per side, {MaxStaticImagePixels} pixels).");
                    }
                    Object.Destroy(tex);
                    Destroy(gameObject);
                    return;
                }
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

        private const int MaxRedirectHops = 3;

        // Security: GET with redirects off; each 3xx hop is re-checked against the host allow-list (max 3 hops),
        // the body is capped while reading, and only image/gif, png, jpeg or webp responses are accepted.
        private IEnumerator FetchAllowed(string url, int timeoutSeconds, System.Action<byte[]> done)
        {
            byte[] result = null;
            for (int hop = 0; hop <= MaxRedirectHops; hop++)
            {
                if (!IsAllowedMediaUrl(url))
                {
                    Debug.LogWarning($"[ChatMedia] Refused: hop {hop} host not allowed ({SafeHost(url)}).");
                    break;
                }
                string next = null;
                using (var request = UnityWebRequest.Get(url))
                {
                    request.redirectLimit = 0;
                    request.downloadHandler = new CappedDownloadHandler(MaxDownloadBytes);
                    request.timeout = timeoutSeconds;
                    request.SetRequestHeader("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) VerdantsAscent/1.0");

                    yield return request.SendWebRequest();

                    long code = request.responseCode;
                    if (code >= 300 && code < 400)
                    {
                        string loc = request.GetResponseHeader("Location");
                        if (string.IsNullOrEmpty(loc) || !System.Uri.TryCreate(new System.Uri(url), loc, out var target))
                        {
                            Debug.LogWarning($"[ChatMedia] Refused: redirect {code} without a usable Location.");
                            break;
                        }
                        next = target.AbsoluteUri;
                    }
                    else if (request.result == UnityWebRequest.Result.Success)
                    {
                        string ct = (request.GetResponseHeader("Content-Type") ?? "").Split(';')[0].Trim().ToLowerInvariant();
                        if (ct != "image/gif" && ct != "image/png" && ct != "image/jpeg" && ct != "image/jpg" && ct != "image/webp")
                        {
                            Debug.LogWarning($"[ChatMedia] Refused: content type '{ct}' is not an image.");
                            break;
                        }
                        var d = request.downloadHandler.data;
                        if (d != null && d.Length > 0 && d.Length <= MaxDownloadBytes) result = d;
                        break;
                    }
                    else
                    {
                        Debug.LogWarning($"[ChatMedia] Download failed ({code}): {request.error} — host {SafeHost(url)}");
                        break;
                    }
                }
                if (hop == MaxRedirectHops) { Debug.LogWarning($"[ChatMedia] Refused: more than {MaxRedirectHops} redirects."); break; }
                url = next;
            }
            done(result);
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
                // Security: no oEmbed request to tenor.com (not on the allow-list); build the media.tenor.com link from the GIF id.
                string tenorId = ExtractTenorGifId(url);
                callback(string.IsNullOrEmpty(tenorId) ? null : $"https://media.tenor.com/images/{tenorId}/tenor.gif");
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
                request.timeout = OEmbedTimeoutSeconds;
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


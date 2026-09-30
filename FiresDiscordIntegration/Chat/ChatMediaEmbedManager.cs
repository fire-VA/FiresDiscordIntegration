using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace FiresDiscordIntegration.ChatRelay
{
    /// <summary>
    /// Manages a standalone, always-active overlay container for GIF/image
    /// embeds triggered by chat messages.
    ///
    /// The container lives under Hud.instance (IngameGui) - NOT inside the
    /// chat window hierarchy - so embeds remain visible and their coroutines
    /// keep running even when the player closes chat. This eliminates the
    /// "Coroutine couldn't be started because the game object is inactive"
    /// error entirely.
    ///
    /// Layout:
    ///   Anchor (1,0)-(1,0)  Pivot (1,0)  - bottom-right corner
    ///   Position  X -80  Y 400
    ///   Size      W 713.06   H 783.27
    ///   - Contains a ScrollRect so multiple embeds can stack and scroll.
    ///   - New embeds are added at the bottom; the scroll auto-follows.
    ///   - Max 3 visible embeds. Oldest is removed when a 4th arrives.
    ///   - Each embed self-destructs after 30 seconds.
    /// </summary>
    public static class ChatMediaEmbedManager
    {
        private const int MaxVisibleEmbeds = 3;
        private const float EmbedPadding = 8f;

        // Container positioning and sizing - anchored to top-right so content
        // grows downward from the anchor point, keeping embeds at a fixed screen position.
        private const float ContainerWidth = 713.06f;
        private const float ContainerHeight = 783.27f;
        private const float ContainerPosX = -80f;
        private const float ContainerPosY = -200f;

        private static readonly List<ChatMediaEmbed> _activeEmbeds = new List<ChatMediaEmbed>();

        // UI hierarchy we build once and re-use
        private static GameObject _overlayRoot;
        private static RectTransform _overlayRect;
        private static ScrollRect _scrollRect;
        private static RectTransform _contentRect;

        /// <summary>
        /// Queue an image/GIF embed. Works whether the chat window is open or not.
        /// </summary>
        public static void QueueEmbed(string url, ChatMediaDetector.MediaType mediaType)
        {
            if (string.IsNullOrEmpty(url)) return;

            EnsureOverlay();
            if (_contentRect == null) return;

            // Enforce max visible embeds - remove oldest
            while (_activeEmbeds.Count >= MaxVisibleEmbeds)
            {
                var oldest = _activeEmbeds[0];
                _activeEmbeds.RemoveAt(0);
                if (oldest != null) oldest.Cleanup();
            }

            // Create embed GameObject inside the scroll content
            var go = new GameObject("ChatMediaEmbed");
            go.transform.SetParent(_contentRect, false);

            // LayoutElement so the VerticalLayoutGroup reserves space
            var le = go.AddComponent<LayoutElement>();
            le.preferredWidth = ContainerWidth - EmbedPadding * 2f;
            le.preferredHeight = 200f; // placeholder; ChatMediaEmbed updates this
            le.flexibleWidth = 0f;

            var embed = go.AddComponent<ChatMediaEmbed>();
            embed.Load(url, mediaType);

            _activeEmbeds.Add(embed);

            // Scroll to bottom next frame so the new embed is visible
            ScrollToBottom();
        }

        /// <summary>
        /// Clean up all active embeds (called on chat clear or scene change).
        /// </summary>
        public static void ClearAll()
        {
            foreach (var embed in _activeEmbeds)
            {
                if (embed != null) embed.Cleanup();
            }
            _activeEmbeds.Clear();
        }

        /// <summary>
        /// Remove destroyed embeds from the tracking list and refresh layout.
        /// Called periodically from ChatPatches.Update.
        /// </summary>
        public static void PruneDestroyed()
        {
            int before = _activeEmbeds.Count;
            _activeEmbeds.RemoveAll(e => e == null);

            if (_activeEmbeds.Count != before)
                RebuildLayout();
        }

        /// <summary>
        /// Must be called when Hud is destroyed / scene changes so we
        /// don't hold stale references.
        /// </summary>
        public static void ResetOverlay()
        {
            _activeEmbeds.Clear();
            if (_overlayRoot != null)
                Object.Destroy(_overlayRoot);
            _overlayRoot = null;
            _overlayRect = null;
            _scrollRect = null;
            _contentRect = null;
        }

        // =========================================================
        // Overlay construction
        // =========================================================

        private static void EnsureOverlay()
        {
            // If the root was destroyed (scene change), rebuild
            if (_overlayRoot == null)
            {
                _overlayRect = null;
                _scrollRect = null;
                _contentRect = null;
            }

            if (_overlayRect != null) return;

            // Find a stable, always-active parent.
            // Hud.instance lives on the IngameGui Canvas which is always active.
            Transform parent = null;
            if (Hud.instance != null)
            {
                parent = Hud.instance.transform;
            }
            else if (Chat.instance != null)
            {
                // Fallback: walk up from Chat to find IngameGui
                var t = Chat.instance.transform;
                while (t.parent != null)
                    t = t.parent;
                parent = t;
            }

            if (parent == null) return;

            BuildOverlay(parent);
        }

        private static void BuildOverlay(Transform parent)
        {
            // -- Root container --
            _overlayRoot = new GameObject("VA_ChatMediaOverlay");
            _overlayRoot.transform.SetParent(parent, false);

            _overlayRect = _overlayRoot.AddComponent<RectTransform>();

            // Anchor to top-right corner of screen so content grows downward
            _overlayRect.anchorMin = new Vector2(1f, 1f);
            _overlayRect.anchorMax = new Vector2(1f, 1f);
            _overlayRect.pivot = new Vector2(1f, 1f);
            _overlayRect.sizeDelta = new Vector2(ContainerWidth, ContainerHeight);
            _overlayRect.anchoredPosition = new Vector2(ContainerPosX, ContainerPosY);

            // -- Scroll Rect (masks children, allows vertical scroll) --
            var scrollGo = new GameObject("Scroll");
            scrollGo.transform.SetParent(_overlayRoot.transform, false);

            var scrollRt = scrollGo.AddComponent<RectTransform>();
            scrollRt.anchorMin = Vector2.zero;
            scrollRt.anchorMax = Vector2.one;
            scrollRt.sizeDelta = Vector2.zero;
            scrollRt.offsetMin = Vector2.zero;
            scrollRt.offsetMax = Vector2.zero;

            // Mask so images outside the rect are clipped
            var mask = scrollGo.AddComponent<RectMask2D>();

            _scrollRect = scrollGo.AddComponent<ScrollRect>();
            _scrollRect.horizontal = false;
            _scrollRect.vertical = true;
            _scrollRect.movementType = ScrollRect.MovementType.Clamped;
            _scrollRect.scrollSensitivity = 30f;

            // -- Content area (VerticalLayoutGroup stacks embeds bottom-up) --
            var contentGo = new GameObject("Content");
            contentGo.transform.SetParent(scrollGo.transform, false);

            _contentRect = contentGo.AddComponent<RectTransform>();
            // Anchor to top-left so content grows downward
            _contentRect.anchorMin = new Vector2(0f, 1f);
            _contentRect.anchorMax = new Vector2(1f, 1f);
            _contentRect.pivot = new Vector2(0f, 1f);
            _contentRect.sizeDelta = new Vector2(0f, 0f);

            var vlg = contentGo.AddComponent<VerticalLayoutGroup>();
            vlg.childAlignment = TextAnchor.LowerLeft;
            vlg.spacing = EmbedPadding;
            vlg.padding = new RectOffset(0, 0, 0, (int)EmbedPadding);
            vlg.childForceExpandWidth = false;
            vlg.childForceExpandHeight = false;
            vlg.childControlWidth = false;
            vlg.childControlHeight = false;

            var csf = contentGo.AddComponent<ContentSizeFitter>();
            csf.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
            csf.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            _scrollRect.content = _contentRect;
            _scrollRect.viewport = scrollRt;
        }

        // =========================================================
        // Helpers
        // =========================================================

        private static void ScrollToBottom()
        {
            // ContentSizeFitter needs a frame to recalculate, so we
            // delay the scroll. Canvas.ForceUpdateCanvases() would also
            // work but is heavier.
            if (_scrollRect != null)
            {
                // Setting normalizedPosition in the same frame often works
                // because the layout rebuilds during the Canvas update pass
                // that happens after all scripts have run.
                Canvas.ForceUpdateCanvases();
                _scrollRect.verticalNormalizedPosition = 0f;
            }
        }

        private static void RebuildLayout()
        {
            if (_contentRect != null)
                LayoutRebuilder.ForceRebuildLayoutImmediate(_contentRect);
        }
    }
}

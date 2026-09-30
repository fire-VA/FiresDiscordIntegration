using System.Collections.Generic;
using FiresDiscordIntegration.Discord;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace FiresDiscordIntegration.ChatRelay
{
    /// <summary>
    /// Discord-style autocomplete hint for @-mentions in the in-game chat box.
    /// When the player types '@' followed by some characters in the chat input,
    /// a translucent panel above the input shows the matching Discord role
    /// names from the synced cache (role colors honoured per line).  The panel
    /// is read-only — Valheim hides the cursor while chat is open so we can't
    /// support clicking, which is fine: the player just keeps typing and the
    /// resolver still expands the @mention into the real ping on the way out.
    ///
    /// One instance per Chat instance, created from the ChatPatches Awake
    /// postfix.  Lives as a child of a top-level ScreenSpaceOverlay canvas (not
    /// the chat canvas) so it can't be hidden by chat fade / chat scroll / a
    /// vanilla overlay on top of the chat panel.
    /// </summary>
    public sealed class ChatMentionHint : MonoBehaviour
    {
        // Width matches typical chat-input width on a 1080p monitor.  Tuned
        // so the panel sits flush above the input edge.  Height grows with
        // the number of rows up to MaxHintRows.
        private const int   PanelWidth                 = 360;
        private const int   PanelHorizontalPaddingPx   = 12;
        private const int   PanelVerticalPaddingPx     = 8;
        private const int   RowSpacingPx               = 2;
        private const int   FontSizePx                 = 16;
        private const int   MaxHintRows                = 8;
        private const int   PanelOffsetAboveInputPx    = 10;

        private const string HintGameObjectName        = "FiresDiscord_MentionHint";
        private const string HintLineFallbackColorHex  = "#DDDDDD";

        // Palette mirrors VerdantsAscent.UI.Patches.UITooltipStyleOverride
        // (the RPG-maker hover tooltip) so the @-mention hint reads as
        // "part of the same UI family".  Values copied verbatim so a tweak
        // there can land here in one edit too.  Background is intentionally
        // 0.95 alpha (almost solid) — the original 0.85 let the chat
        // scrollback bleed through and made the role names hard to read
        // (visible in the operator's repro screenshot from 2026-05-14).
        private static readonly Color PanelBackgroundColor = new Color(0.08f, 0.06f, 0.04f, 0.95f);
        private static readonly Color PanelOutlineColor    = new Color(0.5f,  0.4f,  0.25f, 0.8f);
        private static readonly Vector2 PanelOutlineDistance = new Vector2(2f, -2f);
        // Bold gold header — matches the tooltip's TitleColor.  The header
        // is the only non-role-coloured line (it shows "@partial…" while
        // typing) so it gets the title treatment; role rows below keep
        // their per-role Discord colour for readability.
        private static readonly Color HeaderColor          = new Color(1f, 0.85f, 0.4f, 1f);

        private TextMeshProUGUI _bodyText;
        private RectTransform   _panelRect;
        private Image           _background;
        private Canvas          _ownCanvas;
        private bool            _fontAssetReady;

        /// <summary>
        /// Creates the overlay (idempotent — returns the existing instance if
        /// one is already alive).  Called from ChatPatches' Chat.Awake postfix
        /// when the chat UI first comes up.
        /// </summary>
        public static ChatMentionHint EnsureCreated()
        {
            var existing = GameObject.Find(HintGameObjectName);
            if (existing != null)
            {
                var c = existing.GetComponent<ChatMentionHint>();
                if (c != null) return c;
            }
            var go = new GameObject(HintGameObjectName);
            Object.DontDestroyOnLoad(go);
            go.hideFlags = HideFlags.HideAndDontSave;
            return go.AddComponent<ChatMentionHint>();
        }

        private void Awake()
        {
            BuildPanel();
            HidePanel();
        }

        private void BuildPanel()
        {
            // Top-level overlay canvas so the hint can't be hidden by anything
            // else parented under the chat hierarchy.  SortingOrder higher than
            // most vanilla UI so it stays on top of the chat fade.
            _ownCanvas = gameObject.AddComponent<Canvas>();
            _ownCanvas.renderMode  = RenderMode.ScreenSpaceOverlay;
            _ownCanvas.sortingOrder = 5000;
            gameObject.AddComponent<CanvasScaler>().uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;
            gameObject.AddComponent<GraphicRaycaster>().enabled = false; // read-only — no clicks

            // Panel background.
            var panelGo = new GameObject("Panel");
            panelGo.transform.SetParent(transform, false);
            _panelRect = panelGo.AddComponent<RectTransform>();
            _panelRect.anchorMin = new Vector2(0f, 0f);
            _panelRect.anchorMax = new Vector2(0f, 0f);
            _panelRect.pivot     = new Vector2(0f, 0f); // bottom-left of panel anchored to bottom-left of screen
            _panelRect.sizeDelta = new Vector2(PanelWidth, 0); // height set dynamically

            _background = panelGo.AddComponent<Image>();
            _background.color = PanelBackgroundColor;
            // Flat fill (no nine-slice / sprite), so the dark-brown reads as
            // a single solid block instead of taking on whatever vanilla
            // sprite happened to be on the Image's default state.
            _background.type   = Image.Type.Simple;
            _background.sprite = null;

            // Gold outline mirrors the RPG-maker hover tooltip border so the
            // two overlays look like siblings.  2px shadow distance gives the
            // single-edge framed look without the doubled "drop-shadow"
            // appearance of a larger offset.
            var outline = panelGo.AddComponent<Outline>();
            outline.effectColor    = PanelOutlineColor;
            outline.effectDistance = PanelOutlineDistance;
            outline.useGraphicAlpha = true;

            // Body text (colored per-line via TMP rich-text).
            var textGo = new GameObject("Text");
            textGo.transform.SetParent(panelGo.transform, false);
            var textRect = textGo.AddComponent<RectTransform>();
            textRect.anchorMin = new Vector2(0f, 0f);
            textRect.anchorMax = new Vector2(1f, 1f);
            textRect.offsetMin = new Vector2( PanelHorizontalPaddingPx,  PanelVerticalPaddingPx);
            textRect.offsetMax = new Vector2(-PanelHorizontalPaddingPx, -PanelVerticalPaddingPx);

            _bodyText = textGo.AddComponent<TextMeshProUGUI>();
            _bodyText.fontSize     = FontSizePx;
            _bodyText.alignment    = TextAlignmentOptions.TopLeft;
            _bodyText.richText     = true;
            _bodyText.color        = Color.white;
            _bodyText.raycastTarget = false;
            // Font asset is intentionally NOT assigned here — when the hint is
            // built (Chat.Awake postfix), Valheim's chat output isn't always
            // fully alive yet, and writing to TMP's preferredHeight without a
            // bound font asset throws inside MaterialReference..ctor.  We pull
            // the asset from chat.m_output the first time Update() finds it.
        }

        // Inherits the font asset + material from the live chat output so the
        // hint panel renders in the same typeface as the chat scrollback.
        // Returns false until both the chat output TMP component AND its font
        // asset are alive — without the font assigned, TMP's ParseInputText →
        // SetArraySizes path throws NRE inside MaterialReference's constructor
        // every frame the panel tries to lay itself out.
        private bool EnsureFontAssetReady()
        {
            if (_fontAssetReady) return true;
            if (_bodyText == null) return false;

            var chat = Chat.instance;
            var output = chat?.m_output;
            if (output == null) return false;

            var font = output.font;
            if (font == null) return false;

            _bodyText.font = font;
            var sharedMat = output.fontSharedMaterial;
            if (sharedMat != null) _bodyText.fontSharedMaterial = sharedMat;
            _fontAssetReady = true;
            return true;
        }

        private void Update()
        {
            if (Chat.instance == null) { HidePanel(); return; }

            string typedAfterAt = ReadCurrentMentionTokenOrNull(out bool inputIsFocused);
            if (!inputIsFocused || typedAfterAt == null)
            {
                HidePanel();
                return;
            }

            // Keep the panel hidden until the font asset has been wired up.
            // Without it, UpdatePanelText's preferredHeight call NREs inside
            // TMP's MaterialReference..ctor every frame the panel is visible.
            if (!EnsureFontAssetReady()) { HidePanel(); return; }

            var matches = DiscordRoleNameCache.FilterForHint(typedAfterAt, MaxHintRows);
            if (matches.Count == 0)
            {
                HidePanel();
                return;
            }

            UpdatePanelText(typedAfterAt, matches);
            UpdatePanelPosition();
            ShowPanel();
        }

        // Returns the chars the player has typed after the last '@' in the chat
        // input, ONLY if that '@' is the most recent word-boundary character
        // before the caret.  Returns null when the player isn't currently in a
        // @-mention token (no '@' yet, or they've moved past one with a space).
        // Also reports whether the chat input is focused so the hint stays
        // hidden when chat is closed.
        private static string ReadCurrentMentionTokenOrNull(out bool inputIsFocused)
        {
            inputIsFocused = false;

            // Try TMP_InputField first (newer Valheim), then legacy InputField.
            var chat = Chat.instance;
            string text;
            int caretPos;
            bool focused;
            if (!TryReadChatInputState(chat, out text, out caretPos, out focused))
                return null;

            inputIsFocused = focused;
            if (!focused) return null;
            if (string.IsNullOrEmpty(text)) return null;

            if (caretPos < 0) caretPos = text.Length;
            if (caretPos > text.Length) caretPos = text.Length;

            // Walk backwards from the caret until we hit '@' or a word-break.
            // Acceptable word-token chars after the '@' are \w (letters/digits/_)
            // plus '-' to mirror the resolver's mention regex.
            int scan = caretPos - 1;
            while (scan >= 0)
            {
                char c = text[scan];
                if (c == '@')
                    return text.Substring(scan + 1, caretPos - scan - 1);
                if (!IsMentionTokenChar(c))
                    return null;
                scan--;
            }
            return null;
        }

        private static bool IsMentionTokenChar(char c)
            => char.IsLetterOrDigit(c) || c == '_' || c == '-';

        // Valheim's chat input is a GuiInputField (from gui_framework.dll) which
        // exposes the same .text / .caretPosition / .isFocused surface as
        // UnityEngine.UI.InputField — Valheim ships it as a thin custom subclass
        // for their gamepad-friendly input handling, not for any field rename.
        private static bool TryReadChatInputState(Chat chat, out string text, out int caret, out bool focused)
        {
            text = null; caret = 0; focused = false;
            if (chat == null || chat.m_input == null) return false;

            var input = chat.m_input;
            text    = input.text;
            caret   = input.caretPosition;
            focused = input.isFocused;
            return true;
        }

        private void UpdatePanelText(string typedAfterAt, List<DiscordRoleSummary> matches)
        {
            var sb = new System.Text.StringBuilder();
            string header = string.IsNullOrEmpty(typedAfterAt)
                ? "Discord roles you can @mention:"
                : $"@{typedAfterAt}…";
            sb.Append("<color=").Append(HexFor(HeaderColor)).Append(">").Append(header).Append("</color>");
            foreach (var role in matches)
            {
                sb.Append('\n');
                string color = role.ColorHex ?? HintLineFallbackColorHex;
                sb.Append("<color=").Append(color).Append(">@").Append(role.Name).Append("</color>");
            }
            _bodyText.text = sb.ToString();

            // Match panel height to text height.  TMP recomputes preferredHeight
            // on text assignment; we add the vertical padding back in.
            _bodyText.ForceMeshUpdate();
            float textHeight = _bodyText.preferredHeight;
            float panelHeight = textHeight + (PanelVerticalPaddingPx * 2) + (matches.Count * RowSpacingPx);
            _panelRect.sizeDelta = new Vector2(PanelWidth, panelHeight);
        }

        private void UpdatePanelPosition()
        {
            // Anchor bottom-left of panel to bottom-left of screen, then shift
            // up by the chat input's height + an air gap so the panel sits
            // immediately above the input rect.
            RectTransform inputRect = TryGetChatInputRect();
            float inputBottomY = inputRect != null
                ? GetWorldBottomY(inputRect)
                : 40f; // sensible fallback if reflection fails

            _panelRect.anchoredPosition = new Vector2(
                inputRect != null ? GetWorldLeftX(inputRect) : 20f,
                inputBottomY + (inputRect != null ? inputRect.rect.height : 30f) + PanelOffsetAboveInputPx);
        }

        private static RectTransform TryGetChatInputRect()
        {
            var chat = Chat.instance;
            if (chat == null || chat.m_input == null) return null;
            return chat.m_input.transform as RectTransform;
        }

        private static float GetWorldBottomY(RectTransform rt)
        {
            var corners = new Vector3[4];
            rt.GetWorldCorners(corners);
            return corners[0].y; // bottom-left corner
        }

        private static float GetWorldLeftX(RectTransform rt)
        {
            var corners = new Vector3[4];
            rt.GetWorldCorners(corners);
            return corners[0].x;
        }

        private static string HexFor(Color c)
        {
            return string.Format("#{0:X2}{1:X2}{2:X2}",
                (byte)(c.r * 255), (byte)(c.g * 255), (byte)(c.b * 255));
        }

        private void HidePanel()
        {
            if (_panelRect == null) return;
            if (_panelRect.gameObject.activeSelf) _panelRect.gameObject.SetActive(false);
        }

        private void ShowPanel()
        {
            if (_panelRect == null) return;
            if (!_panelRect.gameObject.activeSelf) _panelRect.gameObject.SetActive(true);
        }
    }
}

using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace FiresDiscordIntegration.Discord
{
    /// <summary>
    /// Converts Discord emoji shortcodes (e.g. :fire:) to Unicode emoji characters
    /// before messages are relayed into Valheim chat.
    ///
    /// Also strips Discord custom emoji markup (&lt;:name:id&gt; / &lt;a:name:id&gt;)
    /// down to the readable name since custom emoji images can't render in-game.
    /// </summary>
    public static class DiscordEmojiConverter
    {
        // Pattern for Discord custom/animated emoji: <:name:123456> or <a:name:123456>
        private static readonly Regex CustomEmojiRegex = new Regex(
            @"<a?:(\w+):\d+>", RegexOptions.Compiled);

        // Pattern for standard shortcodes: :fire: :heart: etc.
        private static readonly Regex ShortcodeRegex = new Regex(
            @":([a-zA-Z0-9_+-]+):", RegexOptions.Compiled);

        /// <summary>
        /// Common Discord emoji shortcodes mapped to their Unicode characters.
        /// Covers the most frequently used emoji in gaming/chat contexts.
        /// </summary>
        private static readonly Dictionary<string, string> ShortcodeMap =
            new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase)
        {
            // Smileys & People
            { "grinning", "\U0001F600" },
            { "smile", "\U0001F604" },
            { "smiley", "\U0001F603" },
            { "grin", "\U0001F601" },
            { "laughing", "\U0001F606" },
            { "satisfied", "\U0001F606" },
            { "sweat_smile", "\U0001F605" },
            { "rofl", "\U0001F923" },
            { "joy", "\U0001F602" },
            { "slightly_smiling_face", "\U0001F642" },
            { "wink", "\U0001F609" },
            { "blush", "\U0001F60A" },
            { "innocent", "\U0001F607" },
            { "heart_eyes", "\U0001F60D" },
            { "kissing_heart", "\U0001F618" },
            { "stuck_out_tongue_winking_eye", "\U0001F61C" },
            { "stuck_out_tongue", "\U0001F61B" },
            { "thinking", "\U0001F914" },
            { "thinking_face", "\U0001F914" },
            { "shushing_face", "\U0001F92B" },
            { "sunglasses", "\U0001F60E" },
            { "nerd", "\U0001F913" },
            { "nerd_face", "\U0001F913" },
            { "clown", "\U0001F921" },
            { "clown_face", "\U0001F921" },
            { "sob", "\U0001F62D" },
            { "cry", "\U0001F622" },
            { "angry", "\U0001F620" },
            { "rage", "\U0001F621" },
            { "scream", "\U0001F631" },
            { "skull", "\U0001F480" },
            { "skull_crossbones", "\u2620\uFE0F" },
            { "ghost", "\U0001F47B" },
            { "alien", "\U0001F47D" },
            { "robot", "\U0001F916" },
            { "poop", "\U0001F4A9" },
            { "hankey", "\U0001F4A9" },
            { "wave", "\U0001F44B" },
            { "raised_hands", "\U0001F64C" },
            { "clap", "\U0001F44F" },
            { "thumbsup", "\U0001F44D" },
            { "+1", "\U0001F44D" },
            { "thumbup", "\U0001F44D" },
            { "thumbsdown", "\U0001F44E" },
            { "-1", "\U0001F44E" },
            { "punch", "\U0001F44A" },
            { "fist", "\u270A" },
            { "muscle", "\U0001F4AA" },
            { "pray", "\U0001F64F" },
            { "point_up", "\u261D\uFE0F" },
            { "point_down", "\U0001F447" },
            { "point_left", "\U0001F448" },
            { "point_right", "\U0001F449" },
            { "middle_finger", "\U0001F595" },
            { "ok_hand", "\U0001F44C" },
            { "eyes", "\U0001F440" },
            { "eye", "\U0001F441\uFE0F" },
            { "tongue", "\U0001F445" },
            { "lips", "\U0001F444" },
            { "100", "\U0001F4AF" },
            { "salute", "\U0001FAE1" },
            { "saluting_face", "\U0001FAE1" },

            // Hearts & Symbols
            { "heart", "\u2764\uFE0F" },
            { "red_heart", "\u2764\uFE0F" },
            { "orange_heart", "\U0001F9E1" },
            { "yellow_heart", "\U0001F49B" },
            { "green_heart", "\U0001F49A" },
            { "blue_heart", "\U0001F499" },
            { "purple_heart", "\U0001F49C" },
            { "black_heart", "\U0001F5A4" },
            { "white_heart", "\U0001F90D" },
            { "broken_heart", "\U0001F494" },
            { "heartbeat", "\U0001F493" },
            { "sparkling_heart", "\U0001F496" },
            { "two_hearts", "\U0001F495" },
            { "revolving_hearts", "\U0001F49E" },
            { "cupid", "\U0001F498" },
            { "gift_heart", "\U0001F49D" },
            { "heartpulse", "\U0001F497" },
            { "star", "\u2B50" },
            { "star2", "\U0001F31F" },
            { "sparkles", "\u2728" },
            { "dizzy", "\U0001F4AB" },
            { "boom", "\U0001F4A5" },
            { "collision", "\U0001F4A5" },
            { "anger", "\U0001F4A2" },
            { "exclamation", "\u2757" },
            { "question", "\u2753" },
            { "x", "\u274C" },
            { "o", "\u2B55" },
            { "check", "\u2705" },
            { "white_check_mark", "\u2705" },
            { "heavy_check_mark", "\u2714\uFE0F" },
            { "ballot_box_with_check", "\u2611\uFE0F" },
            { "warning", "\u26A0\uFE0F" },
            { "no_entry", "\u26D4" },
            { "no_entry_sign", "\U0001F6AB" },
            { "zzz", "\U0001F4A4" },

            // Fire, Weather & Nature
            { "fire", "\U0001F525" },
            { "flame", "\U0001F525" },
            { "droplet", "\U0001F4A7" },
            { "sweat_drops", "\U0001F4A6" },
            { "snowflake", "\u2744\uFE0F" },
            { "cloud", "\u2601\uFE0F" },
            { "sun", "\u2600\uFE0F" },
            { "sunny", "\u2600\uFE0F" },
            { "rainbow", "\U0001F308" },
            { "zap", "\u26A1" },
            { "lightning", "\u26A1" },
            { "thunder", "\u26A1" },
            { "tornado", "\U0001F32A\uFE0F" },
            { "ocean", "\U0001F30A" },
            { "earth_americas", "\U0001F30E" },
            { "globe_with_meridians", "\U0001F310" },
            { "crescent_moon", "\U0001F319" },
            { "full_moon", "\U0001F315" },
            { "comet", "\u2604\uFE0F" },
            { "volcano", "\U0001F30B" },
            { "mountain", "\u26F0\uFE0F" },
            { "mount_fuji", "\U0001F5FB" },
            { "evergreen_tree", "\U0001F332" },
            { "deciduous_tree", "\U0001F333" },
            { "fallen_leaf", "\U0001F342" },
            { "maple_leaf", "\U0001F341" },
            { "mushroom", "\U0001F344" },
            { "herb", "\U0001F33F" },
            { "seedling", "\U0001F331" },
            { "blossom", "\U0001F33C" },
            { "rose", "\U0001F339" },
            { "sunflower", "\U0001F33B" },
            { "tulip", "\U0001F337" },

            // Animals
            { "wolf", "\U0001F43A" },
            { "bear", "\U0001F43B" },
            { "boar", "\U0001F417" },
            { "pig", "\U0001F437" },
            { "deer", "\U0001F98C" },
            { "eagle", "\U0001F985" },
            { "snake", "\U0001F40D" },
            { "dragon", "\U0001F409" },
            { "dragon_face", "\U0001F432" },
            { "bat", "\U0001F987" },
            { "spider", "\U0001F577\uFE0F" },
            { "bug", "\U0001F41B" },
            { "bee", "\U0001F41D" },
            { "fish", "\U0001F41F" },
            { "chicken", "\U0001F414" },
            { "rooster", "\U0001F413" },
            { "dog", "\U0001F436" },
            { "cat", "\U0001F431" },
            { "horse", "\U0001F434" },
            { "unicorn", "\U0001F984" },
            { "crab", "\U0001F980" },
            { "octopus", "\U0001F419" },
            { "whale", "\U0001F40B" },
            { "turtle", "\U0001F422" },
            { "frog", "\U0001F438" },
            { "monkey_face", "\U0001F435" },
            { "gorilla", "\U0001F98D" },
            { "lion", "\U0001F981" },
            { "lion_face", "\U0001F981" },
            { "fox", "\U0001F98A" },
            { "fox_face", "\U0001F98A" },
            { "crow", "\U0001F426\u200D\u2B1B" },
            { "bird", "\U0001F426" },

            // Food & Drink
            { "beer", "\U0001F37A" },
            { "beers", "\U0001F37B" },
            { "wine_glass", "\U0001F377" },
            { "cocktail", "\U0001F378" },
            { "mead", "\U0001F37A" },
            { "meat_on_bone", "\U0001F356" },
            { "poultry_leg", "\U0001F357" },
            { "steak", "\U0001F969" },
            { "cut_of_meat", "\U0001F969" },
            { "bread", "\U0001F35E" },
            { "cheese", "\U0001F9C0" },
            { "apple", "\U0001F34E" },
            { "mushroom_food", "\U0001F344" },
            { "pie", "\U0001F967" },
            { "honey_pot", "\U0001F36F" },
            { "egg", "\U0001F95A" },
            { "fish_food", "\U0001F41F" },
            { "cooking", "\U0001F373" },
            { "fork_and_knife", "\U0001F374" },
            { "pizza", "\U0001F355" },
            { "hamburger", "\U0001F354" },
            { "taco", "\U0001F32E" },
            { "cake", "\U0001F370" },
            { "coffee", "\u2615" },

            // Weapons & Objects (RPG/Viking themed)
            { "crossed_swords", "\u2694\uFE0F" },
            { "swords", "\u2694\uFE0F" },
            { "dagger", "\U0001F5E1\uFE0F" },
            { "dagger_knife", "\U0001F5E1\uFE0F" },
            { "shield", "\U0001F6E1\uFE0F" },
            { "bow_and_arrow", "\U0001F3F9" },
            { "axe", "\U0001FA93" },
            { "hammer", "\U0001F528" },
            { "pick", "\u26CF\uFE0F" },
            { "wrench", "\U0001F527" },
            { "gear", "\u2699\uFE0F" },
            { "bomb", "\U0001F4A3" },
            { "trident", "\U0001F531" },
            { "crown", "\U0001F451" },
            { "gem", "\U0001F48E" },
            { "ring", "\U0001F48D" },
            { "trophy", "\U0001F3C6" },
            { "medal", "\U0001F3C5" },
            { "military_medal", "\U0001F396\uFE0F" },
            { "scroll", "\U0001F4DC" },
            { "map", "\U0001F5FA\uFE0F" },
            { "world_map", "\U0001F5FA\uFE0F" },
            { "compass", "\U0001F9ED" },
            { "anchor", "\u2693" },
            { "boat", "\u26F5" },
            { "sailboat", "\u26F5" },
            { "canoe", "\U0001F6F6" },
            { "ship", "\U0001F6A2" },
            { "skull_and_crossbones", "\u2620\uFE0F" },
            { "coffin", "\u26B0\uFE0F" },
            { "key", "\U0001F511" },
            { "lock", "\U0001F512" },
            { "chains", "\u26D3\uFE0F" },
            { "magic_wand", "\U0001FA84" },
            { "crystal_ball", "\U0001F52E" },
            { "hourglass", "\u231B" },
            { "shield2", "\U0001F6E1\uFE0F" },

            // Buildings & Places
            { "house", "\U0001F3E0" },
            { "castle", "\U0001F3F0" },
            { "european_castle", "\U0001F3F0" },
            { "tent", "\u26FA" },
            { "camping", "\U0001F3D5\uFE0F" },
            { "house_with_garden", "\U0001F3E1" },
            { "church", "\u26EA" },

            // Activities & Celebration
            { "tada", "\U0001F389" },
            { "confetti_ball", "\U0001F38A" },
            { "party_popper", "\U0001F389" },
            { "ribbon", "\U0001F380" },
            { "gift", "\U0001F381" },
            { "balloon", "\U0001F388" },
            { "fireworks", "\U0001F386" },
            { "sparkler", "\U0001F387" },
            { "video_game", "\U0001F3AE" },
            { "joystick", "\U0001F579\uFE0F" },
            { "game_die", "\U0001F3B2" },
            { "dart", "\U0001F3AF" },
            { "bullseye", "\U0001F3AF" },
            { "musical_note", "\U0001F3B5" },
            { "notes", "\U0001F3B6" },
            { "headphones", "\U0001F3A7" },

            // Misc commonly used
            { "gg", "\U0001F3AE" },
            { "lol", "\U0001F602" },
            { "rip", "\U0001FAA6" },
            { "headstone", "\U0001FAA6" },
            { "coffin2", "\u26B0\uFE0F" },
            { "flag_white", "\U0001F3F3\uFE0F" },
            { "pirate_flag", "\U0001F3F4\u200D\u2620\uFE0F" },
            { "triangular_flag_on_post", "\U0001F6A9" },
            { "crossed_flags", "\U0001F38C" },
            { "checkered_flag", "\U0001F3C1" },
            { "mega", "\U0001F4E3" },
            { "loudspeaker", "\U0001F4E2" },
            { "bell", "\U0001F514" },
            { "speech_balloon", "\U0001F4AC" },
            { "thought_balloon", "\U0001F4AD" },
            { "up", "\u2B06\uFE0F" },
            { "down", "\u2B07\uFE0F" },
            { "left", "\u2B05\uFE0F" },
            { "right", "\u27A1\uFE0F" },
            { "arrow_up", "\u2B06\uFE0F" },
            { "arrow_down", "\u2B07\uFE0F" },
            { "arrow_left", "\u2B05\uFE0F" },
            { "arrow_right", "\u27A1\uFE0F" },
            { "arrows_counterclockwise", "\U0001F504" },
            { "arrows_clockwise", "\U0001F503" },
            { "recycle", "\u267B\uFE0F" },
        };

        /// <summary>
        /// Convert all Discord emoji notation in a message to Unicode characters.
        /// Handles both custom emoji (&lt;:name:id&gt;) and standard shortcodes (:name:).
        /// </summary>
        public static string ConvertEmoji(string message)
        {
            if (string.IsNullOrEmpty(message)) return message;

            // First: strip Discord custom/animated emoji to just the name
            // <:pepe:123456789> -> pepe, <a:dance:123456> -> dance
            message = CustomEmojiRegex.Replace(message, match =>
            {
                string name = match.Groups[1].Value;
                // Check if the custom emoji name also matches a standard shortcode
                if (ShortcodeMap.TryGetValue(name, out string unicode))
                    return unicode;
                return name;
            });

            // Second: convert standard shortcodes to Unicode emoji
            message = ShortcodeRegex.Replace(message, match =>
            {
                string name = match.Groups[1].Value;
                if (ShortcodeMap.TryGetValue(name, out string unicode))
                    return unicode;
                // Unknown shortcode - leave as-is
                return match.Value;
            });

            return message;
        }
    }
}

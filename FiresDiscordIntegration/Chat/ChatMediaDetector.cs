using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace FiresDiscordIntegration.ChatRelay
{
    /// <summary>
    /// Detects image/GIF URLs in chat message text.
    /// Returns a list of URLs and their media types for embedding.
    /// </summary>
    public static class ChatMediaDetector
    {
        public enum MediaType
        {
            StaticImage,
            AnimatedGif
        }

        public struct DetectedMedia
        {
            public string Url;
            public MediaType Type;
        }

        // Match URLs ending with image extensions or from known CDNs
        private static readonly Regex UrlPattern = new Regex(
            @"(https?://\S+\.(?:png|jpe?g|gif|webp)(?:\?\S*)?)" +
            @"|(https?://(?:media\.discordapp\.net|cdn\.discordapp\.com)/\S+)" +
            @"|(https?://media\.tenor\.com/\S+)" +
            @"|(https?://tenor\.com/view/\S+)" +
            @"|(https?://(?:i\.)?giphy\.com/\S+)" +
            @"|(https?://(?:media\.)?giphy\.com/media/\S+)" +
            @"|(https?://klipy\.com/gifs/\S+)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// Extract all image/GIF URLs from a chat message string.
        /// </summary>
        public static List<DetectedMedia> ExtractImageUrls(string text)
        {
            var results = new List<DetectedMedia>();
            if (string.IsNullOrEmpty(text)) return results;

            var matches = UrlPattern.Matches(text);
            foreach (Match match in matches)
            {
                string url = match.Value.TrimEnd('.', ',', ')', ']', '>');
                var type = ClassifyUrl(url);
                results.Add(new DetectedMedia { Url = url, Type = type });
            }

            return results;
        }

        /// <summary>
        /// Remove all detected media URLs from the text, leaving the rest of the message.
        /// </summary>
        public static string StripMediaUrls(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            return UrlPattern.Replace(text, "").Trim();
        }

        /// <summary>
        /// Determine if a URL points to a static image or animated GIF.
        /// </summary>
        private static MediaType ClassifyUrl(string url)
        {
            string lower = url.ToLowerInvariant();

            if (lower.Contains(".gif"))
                return MediaType.AnimatedGif;

            if (lower.Contains("tenor.com"))
                return MediaType.AnimatedGif;

            if (lower.Contains("giphy.com"))
                return MediaType.AnimatedGif;

            if (lower.Contains("klipy.com"))
                return MediaType.AnimatedGif;

            return MediaType.StaticImage;
        }
    }
}

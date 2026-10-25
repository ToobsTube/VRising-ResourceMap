using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace ResourceMap;

// Posts a dump file to a Discord channel through a webhook. Outgoing HTTPS only, so it works from hosted servers
// without opening ports. Runs on the dump's background thread, never on the game's main thread.
public static class DiscordPoster
{
    // Discord's upload limit for servers without boosts.
    const long MaxUploadBytes = 10L * 1024 * 1024;

    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };

    // Returns a short status for the dump summary, or null when no webhook is set. Never throws.
    public static string Post(string file, string uploadName, string caption)
    {
        string url = Plugin.DiscordWebhookUrl.Value.Trim();
        if (url.Length == 0) return null;
        if (!url.StartsWith("https://discord.com/api/webhooks/") && !url.StartsWith("https://discordapp.com/api/webhooks/"))
            return "Discord: the webhook link doesn't look like a Discord webhook, so nothing was posted.";

        try
        {
            var size = new FileInfo(file).Length;
            if (size > MaxUploadBytes) return $"Discord: the file is {size / 1048576.0:0.0} MB, over Discord's 10 MB limit, so it wasn't posted.";

            using var form = new MultipartFormDataContent();
            form.Add(new StringContent(JsonSerializer.Serialize(new { content = caption })), "payload_json");
            var bytes = new ByteArrayContent(File.ReadAllBytes(file));
            bytes.Headers.ContentType = new MediaTypeHeaderValue("application/gzip");
            form.Add(bytes, "files[0]", uploadName);

            using var response = Http.PostAsync(url, form).GetAwaiter().GetResult();
            return response.IsSuccessStatusCode
                ? "Posted to Discord."
                : $"Discord upload failed ({(int)response.StatusCode} {response.ReasonPhrase}).";
        }
        catch (Exception e)
        {
            // Don't include the URL: it works like a password for the channel.
            return $"Discord upload failed: {e.GetBaseException().Message}";
        }
    }
}

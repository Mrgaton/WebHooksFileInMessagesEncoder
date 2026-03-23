using DSFiles_Shared;
using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DSFiles_Server.Helpers
{
    internal static class DSFilesHelper
    {
        private static readonly ConcurrentDictionary<string, (string refreshedUrl, DateTime time)> attachementsCache = new();
        private static readonly ConcurrentDictionary<string, long> lengthCache = new();

        private static AuthenticationHeaderValue discordAuth = new AuthenticationHeaderValue("Bot", Environment.GetEnvironmentVariable("TOKEN") ?? throw new ArgumentNullException("Token environment variable is null"));

        private static readonly int maxCacheSize = 12 * 1000;

        public static async Task<long> GetAttachmentSize(string url)
        {
            string trimedUrl = url.Split('?')[0];

            if (lengthCache.ContainsKey(trimedUrl))
            {
                return lengthCache[trimedUrl];
            }

            using (HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Head, url))
            using (HttpResponseMessage res = await Program.client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead))
            {
                if (!res.IsSuccessStatusCode)
                {
                    throw new Exception($"Couldn’t fetch attachment size (HTTP {(int)res.StatusCode})");
                }

                var contentLength = res.Content.Headers.ContentLength;

                if (contentLength == null)
                {
                    throw new Exception("ContentLength was not specified in head of attachement");
                }

                if (lengthCache.Count >= maxCacheSize)
                {
                    var oldestKey = lengthCache.Keys.First();
                    lengthCache.TryRemove(oldestKey, out _);
                }

                lengthCache[trimedUrl] = (long)contentLength;

                return (long)contentLength;
            }
        }

        public static async Task<string[]> RefreshUrls(string[] urls)
        {
            if (urls.Length > 50) throw new ArgumentOutOfRangeException(nameof(urls), "Urls length cant be bigger than 50");

            string[] refreshedUrls = new string[urls.Length];

            var urlsToRefresh = new HashSet<string>();

            for (int i = 0; i < urls.Length; i++)
            {
                var url = urls[i];

                if (attachementsCache.TryGetValue(url, out var info))
                {
                    if ((DateTime.Now - info.time).TotalMinutes >= (23 * 60) + 55)
                    {
                        attachementsCache.TryRemove(url, out _);
                        urlsToRefresh.Add(url);
                    }
                    else
                    {
                        refreshedUrls[i] = url + info.refreshedUrl;
                    }
                }
                else
                {
                    urlsToRefresh.Add(url);
                }
            }

            if (urlsToRefresh.Count > 0)
            {
                var refreshedUrlsFromApi = await RefreshUrlsCore(urlsToRefresh.ToArray());

                for (int i = 0; i < urls.Length; i++)
                {
                    if (refreshedUrls[i] == null)
                    {
                        var originalUrl = urls[i];

                        if (refreshedUrlsFromApi.TryGetValue(originalUrl, out var refreshedUrl))
                        {
                            if (attachementsCache.Count >= maxCacheSize)
                            {
                                var oldestKey = attachementsCache.Keys.FirstOrDefault();
                                if (oldestKey != null) attachementsCache.TryRemove(oldestKey, out _);
                            }

                            if (!string.IsNullOrEmpty(refreshedUrl) && refreshedUrl.StartsWith("https://", StringComparison.InvariantCultureIgnoreCase))
                            {
                                var parts = refreshedUrl.Split('?');
                                if (parts.Length > 1)
                                {
                                    attachementsCache[originalUrl] = ('?' + parts[1], DateTime.Now);
                                }
                            }

                            refreshedUrls[i] = refreshedUrl;
                        }
                        else
                        {
                            refreshedUrls[i] = originalUrl;
                        }
                    }
                }
            }

            return refreshedUrls;
        }

        private static async Task<Dictionary<string,string>> RefreshUrlsCore(string[] urls)
        {
            if (urls.Length > 50) throw new ArgumentOutOfRangeException(nameof(urls), "Urls length cant be bigger than 50");

            var data = JsonSerializer.Serialize(new { attachment_urls = urls });

            using (HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Post, "https://canary.discord.com/api/v9/attachments/refresh-urls")
            {
                Headers = { Authorization = discordAuth },
                Content = new StringContent(data, Encoding.UTF8, "application/json")
            })
            {
                using (HttpResponseMessage res = await Program.client.SendAsync(req))
                {
                    var str = await res.Content.ReadAsStringAsync();

                    return JsonNode.Parse(str)["refreshed_urls"]
                        .AsArray()
                        .ToDictionary(
                            element => (string)element["original"],
                            element => (string)element["refreshed"]
                        );
                }
            }
        }

        public static string EncodeAttachementName(ulong channelId, int index, int amount) => (BitConverter.GetBytes((channelId) ^ (ulong)index ^ (ulong)amount)).ToBase64Url().TrimStart('_') + '_' + (amount - index);
    }
}
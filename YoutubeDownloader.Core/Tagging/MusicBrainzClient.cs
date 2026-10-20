using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using JsonExtensions.Reading;
using PowerKit;
using YoutubeDownloader.Core.Utils;

namespace YoutubeDownloader.Core.Tagging;

internal class MusicBrainzClient
{
    // MusicBrainz allows 1 request per second and responds with 503 when exceeded
    // https://musicbrainz.org/doc/MusicBrainz_API/Rate_Limiting
    private static readonly ThrottleLock ThrottleLock = new(TimeSpan.FromSeconds(1));

    // Returns null instead of throwing when the service keeps declining requests,
    // since music metadata is optional.
    private static async Task<JsonElement?> TryGetJsonAsync(
        string url,
        CancellationToken cancellationToken
    )
    {
        const int maxRetries = 3;

        for (var retry = 0; ; retry++)
        {
            await ThrottleLock.WaitAsync(cancellationToken);
            using var response = await Http.Client.GetAsync(url, cancellationToken);

            if (response.StatusCode == HttpStatusCode.ServiceUnavailable && retry < maxRetries)
            {
                await Task.Delay(
                    response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(2 << retry),
                    cancellationToken
                );

                continue;
            }

            if (!response.IsSuccessStatusCode)
                return null;

            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            using var document = JsonDocument.Parse(content);

            return document.RootElement.Clone();
        }
    }

    public async IAsyncEnumerable<MusicBrainzRecording> SearchRecordingsAsync(
        string query,
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        var url =
            "https://musicbrainz.org/ws/2/recording/"
            + "?version=2"
            + "&fmt=json"
            + "&dismax=true"
            + "&limit=100"
            + $"&query={Uri.EscapeDataString(query)}";

        var json = await TryGetJsonAsync(url, cancellationToken);
        if (json is null)
            yield break;

        var recordingsJson =
            json.Value.GetPropertyOrNull("recordings")?.EnumerateArrayOrNull() ?? default;

        foreach (var recordingJson in recordingsJson)
        {
            var artist = recordingJson
                .GetPropertyOrNull("artist-credit")
                ?.EnumerateArrayOrNull()
                ?.FirstOrDefault()
                .GetPropertyOrNull("name")
                ?.GetNonWhiteSpaceStringOrNull();

            if (string.IsNullOrWhiteSpace(artist))
                continue;

            var artistSort = recordingJson
                .GetPropertyOrNull("artist-credit")
                ?.EnumerateArrayOrNull()
                ?.FirstOrDefault()
                .GetPropertyOrNull("artist")
                ?.GetPropertyOrNull("sort-name")
                ?.GetNonWhiteSpaceStringOrNull();

            var title = recordingJson.GetPropertyOrNull("title")?.GetNonWhiteSpaceStringOrNull();

            if (string.IsNullOrWhiteSpace(title))
                continue;

            var album = recordingJson
                .GetPropertyOrNull("releases")
                ?.EnumerateArrayOrNull()
                ?.FirstOrDefault()
                .GetPropertyOrNull("title")
                ?.GetNonWhiteSpaceStringOrNull();

            yield return new MusicBrainzRecording(artist, artistSort, title, album);
        }
    }
}

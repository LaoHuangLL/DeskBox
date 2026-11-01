using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using DeskBox.Helpers;
using DeskBox.Models;

namespace DeskBox.Services;

/// <summary>
/// Fetches weather from the public moji.com web site (tianqi.moji.com).
/// No API key is involved: the site's own pages and the JSON endpoints its
/// scripts call are used directly.
///
/// The endpoints require a city-bound session. The flow is:
///   1. /api/geolocate/{longitude}/{latitude} returns the city page path
///      (e.g. "/weather/china/hubei/wuhan") and hands out a PHPSESSID.
///   2. Requesting that city page makes the server bind the session to the
///      city; without it the two JSON endpoints below answer with
///      {"hour24":false} / {"code":1}.
///   3. /index/getHour24 returns the 24-hour forecast.
///   4. /api/month/{yyyy-MM} returns the month calendar HTML with one row
///      per day (weather word, low/high temperature, wind).
///
/// The city page body is never parsed (it is GBK, and everything needed is
/// available from the JSON endpoints), so no code-page provider is required.
/// </summary>
internal static partial class MojiWeatherService
{
    private const string BaseUrl = "https://tianqi.moji.com";
    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36";

    private const int ForecastDayCount = 16;

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Fetches the unified weather payload for the given coordinates, or null
    /// when the site cannot be reached / does not answer with usable data.
    /// </summary>
    public static async Task<WeatherData?> FetchAsync(
        double latitude,
        double longitude,
        CancellationToken cancellationToken)
    {
        var handler = new SocketsHttpHandler
        {
            CookieContainer = new CookieContainer(),
            UseCookies = true,
            AutomaticDecompression = DecompressionMethods.All
        };
        using var client = new HttpClient(handler)
        {
            Timeout = RequestTimeout
        };

        try
        {
            string? cityPath = await GetCityPathAsync(client, latitude, longitude, cancellationToken);
            if (string.IsNullOrWhiteSpace(cityPath))
            {
                App.Log("[MojiWeather] geolocate returned no city path");
                return null;
            }

            string cityUrl = BaseUrl + cityPath;
            // Establishes the city-bound session; the body itself is unused.
            await EstablishCitySessionAsync(client, cityUrl, cancellationToken);

            MojiHour24Response? hours = await GetHour24Async(client, cityUrl, cancellationToken);
            if (hours?.Hour24 is not { Count: > 0 })
            {
                App.Log("[MojiWeather] getHour24 returned no hourly rows");
                return null;
            }

            List<MojiDayRow> days = await GetMonthDaysAsync(client, cityUrl, cancellationToken);

            return BuildWeatherData(latitude, longitude, hours, days);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            App.Log($"[MojiWeather] fetch failed: {ex.Message}");
            return null;
        }
        finally
        {
            handler.Dispose();
        }
    }

    // ── Step 1: coordinates → city page path ──

    private static async Task<string?> GetCityPathAsync(
        HttpClient client,
        double latitude,
        double longitude,
        CancellationToken cancellationToken)
    {
        string url = string.Create(
            CultureInfo.InvariantCulture,
            $"{BaseUrl}/api/geolocate/{longitude:F4}/{latitude:F4}");

        using var request = CreateRequest(url, xhr: true);
        using HttpResponseMessage response = await client.SendAsync(
            request,
            HttpCompletionOption.ResponseContentRead,
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        string body = (await response.Content.ReadAsStringAsync(cancellationToken)).Trim();
        if (body.Length == 0 || body == "0" || !body.StartsWith("/weather/", StringComparison.Ordinal))
        {
            return null;
        }

        return body;
    }

    // ── Step 2: bind the session to that city (body intentionally ignored) ──

    private static async Task EstablishCitySessionAsync(
        HttpClient client,
        string cityUrl,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(cityUrl, xhr: false);
        // Drain the body: the server writes the city into the PHP session when
        // the request completes, so the stream must not be abandoned early.
        using HttpResponseMessage response = await client.SendAsync(
            request,
            HttpCompletionOption.ResponseContentRead,
            cancellationToken);
        _ = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        App.Log(
            $"[MojiWeather] city session status={(int)response.StatusCode} city={cityUrl}");
    }

    // ── Step 3: 24-hour forecast ──

    private static async Task<MojiHour24Response?> GetHour24Async(
        HttpClient client,
        string cityUrl,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest($"{BaseUrl}/index/getHour24", xhr: true, referer: cityUrl);
        using HttpResponseMessage response = await client.SendAsync(
            request,
            HttpCompletionOption.ResponseContentRead,
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        string json = await response.Content.ReadAsStringAsync(cancellationToken);
        return JsonSerializer.Deserialize(json, WeatherJsonContext.Default.MojiHour24);
    }

    // ── Step 4: month calendar rows ──

    private static async Task<List<MojiDayRow>> GetMonthDaysAsync(
        HttpClient client,
        string cityUrl,
        CancellationToken cancellationToken)
    {
        var rows = new List<MojiDayRow>();
        DateTime today = DateTime.Today;
        // The calendar covers one month, so a second call is needed when the
        // 16-day window crosses into the next month.
        var months = new List<DateTime> { new(today.Year, today.Month, 1) };
        if (today.Day + ForecastDayCount - 1 > DateTime.DaysInMonth(today.Year, today.Month))
        {
            months.Add(new DateTime(today.Year, today.Month, 1).AddMonths(1));
        }

        foreach (DateTime month in months)
        {
            string url = $"{BaseUrl}/api/month/{month.ToString("yyyy-MM", CultureInfo.InvariantCulture)}";
            using var request = CreateRequest(url, xhr: true, referer: cityUrl);
            try
            {
                using HttpResponseMessage response = await client.SendAsync(
                    request,
                    HttpCompletionOption.ResponseContentRead,
                    cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    App.Log($"[MojiWeather] month {month:yyyy-MM} http={(int)response.StatusCode}");
                    continue;
                }

                string json = await response.Content.ReadAsStringAsync(cancellationToken);
                MojiMonthResponse? payload = JsonSerializer.Deserialize(
                    json,
                    WeatherJsonContext.Default.MojiMonth);
                if (payload?.Code != 0 || string.IsNullOrEmpty(payload.Data?.Html))
                {
                    App.Log($"[MojiWeather] month {month:yyyy-MM} unavailable code={payload?.Code}");
                    continue;
                }

                rows.AddRange(ParseMonthHtml(payload.Data.Html, month));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                App.Log($"[MojiWeather] month {month:yyyy-MM} failed: {ex.Message}");
            }
        }

        return rows
            .Where(row => row.Date >= today)
            .OrderBy(row => row.Date)
            .Take(ForecastDayCount)
            .ToList();
    }

    /// <summary>
    /// Parses one calendar entry per day out of the month HTML:
    /// &lt;li class="item"&gt;&lt;em&gt;01&lt;/em&gt;&lt;b&gt;&lt;img alt="中雨 "&gt;&lt;/b&gt;
    /// &lt;p&gt;9/18°&lt;/p&gt;&lt;p&gt;北风&amp;nbsp;&amp;nbsp;4级&lt;/p&gt;&lt;/li&gt;
    /// </summary>
    internal static List<MojiDayRow> ParseMonthHtml(string html, DateTime month)
    {
        var rows = new List<MojiDayRow>();
        foreach (Match match in MonthItemRegex().Matches(html))
        {
            if (!int.TryParse(match.Groups[1].Value, out int day))
            {
                continue;
            }

            int maxDay = DateTime.DaysInMonth(month.Year, month.Month);
            if (day < 1 || day > maxDay)
            {
                continue;
            }

            string condition = match.Groups[2].Value.Trim();
            string temperature = match.Groups[3].Value.Trim();
            (double low, double high) = ParseTemperatureRange(temperature);

            rows.Add(new MojiDayRow
            {
                Date = new DateTime(month.Year, month.Month, day),
                Condition = condition,
                TemperatureLow = low,
                TemperatureHigh = high
            });
        }

        return rows;
    }

    private static (double Low, double High) ParseTemperatureRange(string text)
    {
        // "9/18°" — low first, then high.
        int slash = text.IndexOf('/');
        if (slash <= 0)
        {
            return (0, 0);
        }

        string lowText = text[..slash].Trim().TrimEnd('°', '°');
        string highText = text[(slash + 1)..].Trim().TrimEnd('°', '°');
        double low = ParseDouble(lowText);
        double high = ParseDouble(highText);
        return (low, high);
    }

    private static double ParseDouble(string text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
            ? value
            : 0;

    // ── Mapping to the unified model ──

    private static WeatherData BuildWeatherData(
        double latitude,
        double longitude,
        MojiHour24Response hours,
        List<MojiDayRow> days)
    {
        var data = new WeatherData
        {
            Latitude = latitude,
            Longitude = longitude,
            Timezone = string.Empty
        };

        MojiHour24Item current = hours.Hour24![0];
        int currentHour = current.Hour;
        data.Current = new WeatherCurrent
        {
            Time = $"{current.Date}T{currentHour:00}:00",
            Temperature = current.Temperature,
            Humidity = current.Humidity,
            ApparentTemperature = current.FeelsLike,
            WeatherCode = ToWmoCode(current.Condition),
            WindSpeed = current.WindSpeed,
            WindDirection = ParseDouble(current.WindDegrees),
            Pressure = 0,
            IsDay = currentHour is >= 6 and < 19 ? 1 : 0
        };

        var hourly = new WeatherHourly();
        foreach (MojiHour24Item item in hours.Hour24)
        {
            hourly.Time.Add($"{item.Date}T{item.Hour:00}:00");
            hourly.Temperature.Add(item.Temperature);
            // moji does not expose an hourly precipitation probability.
            hourly.PrecipitationProbability.Add(0);
            hourly.WeatherCode.Add(ToWmoCode(item.Condition));
            hourly.WindSpeed.Add(item.WindSpeed);
        }

        data.Hourly = hourly;

        if (days.Count > 0)
        {
            var daily = new WeatherDaily();
            foreach (MojiDayRow row in days)
            {
                daily.Time.Add(row.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                daily.WeatherCode.Add(ToWmoCode(row.Condition));
                daily.TemperatureMax.Add(row.TemperatureHigh);
                daily.TemperatureMin.Add(row.TemperatureLow);
                daily.PrecipitationProbabilityMax.Add(0);
                daily.Sunrise.Add(string.Empty);
                daily.Sunset.Add(string.Empty);
                daily.UvIndexMax.Add(0);
            }

            ApplySunriseSunset(daily, hours.Sunset);
            data.Daily = daily;
        }

        return data;
    }

    /// <summary>
    /// The hour endpoint carries today's sunrise/sunset; it is folded onto the
    /// first daily row, which is the only one the widget renders.
    /// </summary>
    private static void ApplySunriseSunset(WeatherDaily daily, MojiSunset? sunset)
    {
        if (sunset is null || daily.Time.Count == 0)
        {
            return;
        }

        string date = daily.Time[0];
        daily.Sunrise[0] = ToLocalIso(date, sunset.Sunrise);
        daily.Sunset[0] = ToLocalIso(date, sunset.Sundown);
    }

    private static string ToLocalIso(string date, string timestamp)
    {
        // The hour endpoint reports "2026-11-02 06:38:00"; only the clock time
        // matters, pinned to the requested day.
        if (DateTime.TryParse(timestamp, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime parsed))
        {
            return $"{date}T{parsed:HH:mm}";
        }

        return string.Empty;
    }

    private static int ToWmoCode(string condition)
    {
        int code = WeatherCodeMapper.DescriptionToWmoCode(condition);
        // Unknown wording falls back to "overcast" rather than a clear sky.
        return code >= 0 ? code : 3;
    }

    private static HttpRequestMessage CreateRequest(string url, bool xhr, string? referer = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        request.Headers.TryAddWithoutValidation("Referer", referer ?? BaseUrl + "/");
        request.Headers.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9");
        if (xhr)
        {
            request.Headers.TryAddWithoutValidation("Accept", "application/json, text/javascript, */*; q=0.01");
            request.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
        }
        else
        {
            request.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,*/*;q=0.8");
        }

        return request;
    }

    [GeneratedRegex(
        "<li class=\"item[^\"]*\">\\s*<em>(\\d{1,2})</em>.*?<img[^>]*alt=\"([^\"]*)\"[^>]*>.*?<p>([^<]*)</p>\\s*<p>([^<]*)</p>",
        RegexOptions.Singleline)]
    private static partial Regex MonthItemRegex();
}

/// <summary>One day parsed from the moji month calendar.</summary>
internal sealed class MojiDayRow
{
    public DateTime Date { get; init; }
    public string Condition { get; init; } = string.Empty;
    public double TemperatureLow { get; init; }
    public double TemperatureHigh { get; init; }
}

// ── Wire models ──

internal sealed class MojiHour24Response
{
    [JsonPropertyName("hour24")]
    public List<MojiHour24Item>? Hour24 { get; set; }

    [JsonPropertyName("sunset")]
    public MojiSunset? Sunset { get; set; }
}

internal sealed class MojiHour24Item
{
    [JsonPropertyName("Fpredict_date")]
    public string Date { get; set; } = string.Empty;

    [JsonPropertyName("Fpredict_hour")]
    public int Hour { get; set; }

    [JsonPropertyName("Ftemp")]
    public double Temperature { get; set; }

    [JsonPropertyName("Ffeelslike")]
    public double FeelsLike { get; set; }

    [JsonPropertyName("Fhumidity")]
    public double Humidity { get; set; }

    [JsonPropertyName("Fcondition")]
    public string Condition { get; set; } = string.Empty;

    [JsonPropertyName("Fwspd")]
    public double WindSpeed { get; set; }

    [JsonPropertyName("Fwdir")]
    public string WindDirection { get; set; } = string.Empty;

    [JsonPropertyName("wind_degrees")]
    public string WindDegrees { get; set; } = string.Empty;

    [JsonPropertyName("wind_level")]
    public int WindLevel { get; set; }
}

internal sealed class MojiSunset
{
    [JsonPropertyName("date")]
    public string Date { get; set; } = string.Empty;

    [JsonPropertyName("sunrise")]
    public string Sunrise { get; set; } = string.Empty;

    [JsonPropertyName("sundown")]
    public string Sundown { get; set; } = string.Empty;
}

internal sealed class MojiMonthResponse
{
    [JsonPropertyName("code")]
    public int Code { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("data")]
    public MojiMonthData? Data { get; set; }
}

internal sealed class MojiMonthData
{
    [JsonPropertyName("month_lunardate")]
    public string LunarDate { get; set; } = string.Empty;

    [JsonPropertyName("html")]
    public string Html { get; set; } = string.Empty;
}

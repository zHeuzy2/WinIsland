using System.Net.Http;
using System.Text.Json;

namespace WinIsland.Services;

/// <summary>
/// Fetches current weather for the machine's approximate location and publishes
/// it to <see cref="AppState.Weather"/> for the idle readout. Location comes from
/// IP geolocation (ip-api.com) and the forecast from Open-Meteo; both are free
/// and require no API key. All network work happens on a background task and is
/// wrapped in try/catch, so being offline simply leaves the last snapshot (or the
/// clock-only fallback) in place.
/// </summary>
public sealed class WeatherService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(15);

    private readonly AppState _state;
    private readonly Action _notify;

    private double _lat, _lon;
    private string _city = "";
    private bool _hasLocation;

    public WeatherService(AppState state, Action notify)
    {
        _state = state;
        _notify = notify;
    }

    public void Start() => _ = Task.Run(LoopAsync);

    private async Task LoopAsync()
    {
        while (true)
        {
            try
            {
                if (!_hasLocation) await ResolveLocationAsync();
                if (_hasLocation) await FetchWeatherAsync();
            }
            catch { /* offline / transient API failure: keep last snapshot */ }

            await Task.Delay(RefreshInterval);
        }
    }

    /// <summary>Approximate lat/lon/city from the public IP (no API key).</summary>
    private async Task ResolveLocationAsync()
    {
        using var doc = await GetJsonAsync("http://ip-api.com/json/?fields=status,lat,lon,city");
        var root = doc.RootElement;
        if (root.TryGetProperty("status", out var st) && st.GetString() != "success") return;

        _lat = root.GetProperty("lat").GetDouble();
        _lon = root.GetProperty("lon").GetDouble();
        _city = root.TryGetProperty("city", out var c) ? (c.GetString() ?? "") : "";
        _hasLocation = true;
    }

    private async Task FetchWeatherAsync()
    {
        string url =
            "https://api.open-meteo.com/v1/forecast" +
            $"?latitude={_lat.ToString(System.Globalization.CultureInfo.InvariantCulture)}" +
            $"&longitude={_lon.ToString(System.Globalization.CultureInfo.InvariantCulture)}" +
            "&current=temperature_2m,weather_code";

        using var doc = await GetJsonAsync(url);
        if (!doc.RootElement.TryGetProperty("current", out var cur)) return;

        double temp = cur.GetProperty("temperature_2m").GetDouble();
        int code = cur.TryGetProperty("weather_code", out var wc) ? wc.GetInt32() : 0;

        _state.Weather = new WeatherSnapshot
        {
            HasData = true,
            TempC = temp,
            WeatherCode = code,
            City = _city,
        };
        _notify();
    }

    private static async Task<JsonDocument> GetJsonAsync(string url)
    {
        using var resp = await Http.GetAsync(url);
        resp.EnsureSuccessStatusCode();
        string body = await resp.Content.ReadAsStringAsync();
        return JsonDocument.Parse(body);
    }
}

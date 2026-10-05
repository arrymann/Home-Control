using System.Globalization;
using System.Text.Json;

namespace HomeControl.Core.Automations;

/// <summary>A place found by name.</summary>
public sealed record GeoPlace(string Name, string? Region, string? Country, double Latitude, double Longitude)
{
    /// <summary>"Berlin, Land Berlin, Germany".</summary>
    public string DisplayName => string.Join(", ", new[] { Name, Region, Country }.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct());

    public GeoLocation ToLocation() => new(Latitude, Longitude, DisplayName);
}

/// <summary>Finds places by name with the free Open-Meteo geocoding API (no key or account needed).</summary>
public sealed class GeocodingClient
{
    private const string Endpoint = "https://geocoding-api.open-meteo.com/v1/search";

    private readonly HttpClient _http;

    public GeocodingClient(HttpClient http)
    {
        _http = http;
    }

    public async Task<IReadOnlyList<GeoPlace>> SearchAsync(string query, CancellationToken cancellationToken)
    {
        query = query.Trim();
        if (query.Length < 2)
        {
            return [];
        }

        var language = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        var url = $"{Endpoint}?name={Uri.EscapeDataString(query)}&count=8&format=json&language={Uri.EscapeDataString(language)}";
        using var response = await _http.GetAsync(url, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
    }

    internal static IReadOnlyList<GeoPlace> Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var places = new List<GeoPlace>();
        foreach (var item in results.EnumerateArray())
        {
            if (item.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String &&
                item.TryGetProperty("latitude", out var latitude) && latitude.TryGetDouble(out var lat) &&
                item.TryGetProperty("longitude", out var longitude) && longitude.TryGetDouble(out var lon))
            {
                places.Add(new GeoPlace(name.GetString()!, Text(item, "admin1"), Text(item, "country"), lat, lon));
            }
        }

        return places;
    }

    private static string? Text(JsonElement item, string property) =>
        item.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}

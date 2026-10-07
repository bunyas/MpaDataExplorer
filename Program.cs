using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Caching.Memory;

var builder = WebApplication.CreateBuilder(args);

// Ensure app listens on port 8080 inside the container
builder.WebHost.UseUrls("http://+:8080");

builder.Services.AddMemoryCache();
builder.Services.AddHttpClient("ObisClient", client =>
{
    string baseUrl = builder.Configuration["OBIS_API_BASE_URL"] ?? "https://api.obis.org/v3/";
    client.BaseAddress = new Uri(baseUrl);
    client.Timeout = TimeSpan.FromSeconds(20);
    client.DefaultRequestHeaders.Add("User-Agent", "UNESCO-OBIS-MPA-Explorer/1.0");
});

var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();

// Simulated Costa Verde National Node (Using OBIS Brazil as a realistic proxy)
const string NationalNodeId = "4bf79a01-65a9-4db6-b37b-18434f26ddfc";

// Reference Phyla to explicitly expose 0-record taxonomic gaps
string[] ReferencePhyla = ["Mollusca", "Arthropoda", "Chordata", "Cnidaria", "Echinodermata", "Annelida", "Porifera", "Rhodophyta", "Chlorophyta"];

var mpas = new List<MpaDef>
{
    new("abrolhos", "Parque Nacional Marinho dos Abrolhos", "Marine National Park", "POLYGON((-39.1 -18.2, -38.6 -18.2, -38.6 -17.8, -39.1 -17.8, -39.1 -18.2))", -18.0, -38.85),
    new("costa-corais", "APA Costa dos Corais", "Environmental Protection Area", "POLYGON((-35.6 -9.5, -34.9 -9.5, -34.9 -8.8, -35.6 -8.8, -35.6 -9.5))", -9.15, -35.25),
    new("noronha", "Fernando de Noronha", "World Heritage Site", "POLYGON((-32.5 -4.0, -32.3 -4.0, -32.3 -3.8, -32.5 -3.8, -32.5 -4.0))", -3.9, -32.4)
};

app.MapGet("/api/mpas", () => Results.Ok(mpas));

app.MapGet("/api/mpa/{id}", async (string id, IHttpClientFactory httpFactory, IMemoryCache cache) =>
{
    var mpa = mpas.FirstOrDefault(m => m.Id == id);
    if (mpa == null) return Results.NotFound();

    string cacheKey = $"mpa_{mpa.Id}";
    if (cache.TryGetValue(cacheKey, out MpaResponse? cached) && cached != null)
        return Results.Ok(cached);

    var client = httpFactory.CreateClient("ObisClient");
    string wkt = Uri.EscapeDataString(mpa.Wkt);

    // Parallel requests to overcome latency constraint
    var tYears = FetchObisJsonAsync(client, $"statistics/years?geometry={wkt}");
    var tFacets = FetchObisJsonAsync(client, $"facet?facets=phylum,node_id,institutionCode&geometry={wkt}");
    var tOcc = FetchObisJsonAsync(client, $"occurrence?geometry={wkt}&size=500");

    await Task.WhenAll(tYears, tFacets, tOcc);

    long totalRecords = tFacets.Result?["total"]?.GetValue<long>() ?? 0;

    // Panel 1: Node & Institution Contribution
    int natRecords = 0;
    var institutions = new List<object>();
    if (tFacets.Result?["results"] is JsonObject resObj)
    {
        if (resObj["node_id"] is JsonArray nArr)
        {
            var natNode = nArr.FirstOrDefault(n => n?["key"]?.ToString() == NationalNodeId);
            natRecords = natNode?["records"]?.GetValue<int>() ?? 0;
        }
        if (resObj["institutionCode"] is JsonArray iArr)
        {
            long instTotal = 0;
            foreach (var inst in iArr.Take(5))
            {
                long count = inst?["records"]?.GetValue<long>() ?? 0;
                instTotal += count;
                institutions.Add(new { Name = inst?["key"]?.ToString() ?? "Unknown", Records = count });
            }
            long missing = totalRecords - instTotal;
            if (missing > 0) institutions.Add(new { Name = "Unrecorded (Metadata Gap)", Records = missing });
        }
    }

    // Panel 2: Continuous Temporal Gaps (2010-2024)
    var rawYears = new Dictionary<int, long>();
    if (tYears.Result is JsonArray yrArr)
    {
        foreach (var item in yrArr)
        {
            int y = item?["year"]?.GetValue<int>() ?? 0;
            long c = item?["records"]?.GetValue<long>() ?? 0;
            if (y > 0) rawYears[y] = c;
        }
    }
    var yearSeries = Enumerable.Range(2010, 15).Select(y => new
    {
        Year = y,
        Records = rawYears.GetValueOrDefault(y, 0),
        IsGap = !rawYears.ContainsKey(y)
    }).ToList();

    // Panel 3: Taxonomic Gaps (against Reference Phyla)
    var rawPhyla = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
    if (tFacets.Result?["results"]?["phylum"] is JsonArray pArr)
    {
        foreach (var item in pArr)
        {
            string p = item?["key"]?.ToString() ?? "";
            long c = item?["records"]?.GetValue<long>() ?? 0;
            if (!string.IsNullOrEmpty(p)) rawPhyla[p] = c;
        }
    }
    var taxonSeries = ReferencePhyla.Select(p => new
    {
        Phylum = p,
        Records = rawPhyla.GetValueOrDefault(p, 0),
        IsGap = !rawPhyla.ContainsKey(p)
    }).OrderByDescending(x => x.Records).ToList();

    // Map Occurrences
    var points = new List<object>();
    if (tOcc.Result?["results"] is JsonArray occArr)
    {
        foreach (var item in occArr)
        {
            double? lat = item?["decimalLatitude"]?.GetValue<double>();
            double? lon = item?["decimalLongitude"]?.GetValue<double>();
            if (lat == null || lon == null) continue;

            string nid = item?["node_id"]?.ToString() ?? "";
            points.Add(new
            {
                Id = item?["id"]?.ToString() ?? "",
                Species = item?["scientificName"]?.ToString() ?? "Unknown",
                Phylum = item?["phylum"]?.ToString() ?? "",
                Date = item?["eventDate"]?.ToString() ?? "",
                Lat = lat,
                Lon = lon,
                Institution = item?["institutionCode"]?.ToString() ?? "Unknown",
                IsNational = nid == NationalNodeId
            });
        }
    }

    var response = new MpaResponse(mpa, totalRecords, natRecords, institutions, yearSeries, taxonSeries, points);
    cache.Set(cacheKey, response, TimeSpan.FromMinutes(30));
    return Results.Ok(response);
});

app.MapGet("/api/mpa/{id}/csv", async (string id, IMemoryCache cache) =>
{
    if (!cache.TryGetValue($"mpa_{id}", out MpaResponse? data) || data == null)
        return Results.BadRequest("Load the MPA in the dashboard first to generate the CSV.");

    var sb = new StringBuilder();
    sb.AppendLine($"\"# Citation: Ocean Biodiversity Information System. Intergovernmental Oceanographic Commission of UNESCO. Occurrences in {data.Mpa.Name}. Accessed {DateTime.UtcNow:yyyy-MM-dd}.\"");
    sb.AppendLine("occurrenceID,scientificName,phylum,eventDate,decimalLatitude,decimalLongitude,institutionCode,isNationalNode");

    foreach (dynamic p in data.Points)
    {
        sb.AppendLine($"\"{p.Id}\",\"{p.Species}\",\"{p.Phylum}\",\"{p.Date}\",{p.Lat.ToString(CultureInfo.InvariantCulture)},{p.Lon.ToString(CultureInfo.InvariantCulture)},\"{p.Institution}\",{p.IsNational}");
    }
    return Results.File(Encoding.UTF8.GetBytes(sb.ToString()), "text/csv", $"OBIS_MPA_{id}.csv");
});

app.Run();

static async Task<JsonNode?> FetchObisJsonAsync(HttpClient client, string url)
{
    try
    {
        var resp = await client.GetAsync(url);
        if (!resp.IsSuccessStatusCode) return null;
        return JsonNode.Parse(await resp.Content.ReadAsStringAsync());
    }
    catch { return null; }
}

public record MpaDef(string Id, string Name, string Designation, string Wkt, double Lat, double Lon);

public record MpaResponse(MpaDef Mpa, long TotalRecords, int NationalRecords, object Institutions, object Years, object Taxa, IEnumerable<dynamic> Points);
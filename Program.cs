using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Caching.Memory;

var builder = WebApplication.CreateBuilder(args);

// Ensure app listens on port 8080 inside the container
/// Note: The "http://+:8080" URL allows the application to listen on all network interfaces (IP addresses) on port 8080. 
/// This is particularly useful in containerized environments where the application may need to accept incoming requests from external sources. By using the "+" wildcard, the application can handle requests regardless of the specific IP address assigned to the container, making it more flexible and accessible.
/// 
builder.WebHost.UseUrls("http://+:8080"); // Listen on all network interfaces (IP addresses) on port 8080 comment this line if you want to use the default port (5000) for local development

// Add services to the container.
builder.Services.AddMemoryCache();
builder.Services.AddHttpClient("ObisClient", client =>
{
    /// The base URL for the OBIS API is configurable via the "OBIS_API_BASE_URL" environment variable. If this variable is not set, it defaults to "https://api.obis.org/v3/". 
    /// This allows for flexibility in testing or using different API endpoints without changing the code.
    /// 
    string baseUrl = builder.Configuration["OBIS_API_BASE_URL"] ?? "https://api.obis.org/v3/";
    client.BaseAddress = new Uri(baseUrl);
    client.Timeout = TimeSpan.FromSeconds(30);// Set a timeout of 30 seconds for API requests to handle potential delays or slow responses from the OBIS API.
    client.DefaultRequestHeaders.Add("User-Agent", "UNESCO-OBIS-MPA-Explorer/1.0");// Set a custom User-Agent header to identify the application when making requests to the OBIS API. This can be useful for monitoring, analytics, or debugging purposes on the server side.
});

var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();

// Simulated Costa Verde National Node (Using OBIS Brazil as a realistic proxy)
// This is used to identify records that are contributed by the national node, which is important for the first panel of the dashboard. 
const string NationalNodeId = "4bf79a01-65a9-4db6-b37b-18434f26ddfc";

// Reference Phyla to explicitly expose 0-record taxonomic gaps
/// This is used to identify taxonomic gaps in the third panel of the dashboard. By comparing the phyla present in the MPA with this reference list, 
/// we can highlight which phyla are missing from the records, indicating potential areas for further research or conservation efforts.
string[] ReferencePhyla = ["Mollusca", "Arthropoda", "Chordata", "Cnidaria", "Echinodermata", "Annelida", "Porifera", "Rhodophyta", "Chlorophyta"];

/// Define Marine Protected Areas (MPAs) with their geometries and metadata
var mpas = new List<MpaDef>
{
    new("abrolhos", "Parque Nacional Marinho dos Abrolhos", "Marine National Park", "POLYGON((-39.1 -18.2, -38.6 -18.2, -38.6 -17.8, -39.1 -17.8, -39.1 -18.2))", -18.0, -38.85),
    new("costa-corais", "APA Costa dos Corais", "Environmental Protection Area", "POLYGON((-35.6 -9.5, -34.9 -9.5, -34.9 -8.8, -35.6 -8.8, -35.6 -9.5))", -9.15, -35.25),
    new("noronha", "Fernando de Noronha", "World Heritage Site", "POLYGON((-32.5 -4.0, -32.3 -4.0, -32.3 -3.8, -32.5 -3.8, -32.5 -4.0))", -3.9, -32.4)
};
// Note: The WKT (Well-Known Text) geometries are simplified for demonstration purposes. In a real application, you would use accurate geometries for each MPA.
app.MapGet("/api/mpas", () => Results.Ok(mpas));

/// This endpoint returns the list of defined MPAs with their metadata, which can be used to populate a selection interface in the dashboard.
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
    // Note: The OBIS API has a latency constraint of 2 seconds, so we fetch the necessary data in parallel to improve performance.
    var tYears = FetchObisJsonAsync(client, $"statistics/years?geometry={wkt}");
    var tFacets = FetchObisJsonAsync(client, $"facet?facets=phylum,node_id,institutionCode&geometry={wkt}");
    var tOcc = FetchObisJsonAsync(client, $"occurrence?geometry={wkt}&size=500");

    await Task.WhenAll(tYears, tFacets, tOcc);

    long totalRecords = tFacets.Result?["total"]?.GetValue<long>() ?? 0;

    // Panel 1: Node & Institution Contribution
    // This panel shows the contribution of records from the national node and various institutions. It helps to understand the sources of data for the MPA.
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
    // This panel shows the number of records per year from 2010 to 2024, highlighting any years with no records (gaps). It helps to identify temporal gaps in the data for the MPA.
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
    // Generate a series of years from 2010 to 2024 and check for gaps in the data
    var yearSeries = Enumerable.Range(2010, 15).Select(y => new
    {
        Year = y,
        Records = rawYears.GetValueOrDefault(y, 0),
        IsGap = !rawYears.ContainsKey(y)
    }).ToList();

    // Panel 3: Taxonomic Gaps (against Reference Phyla)
    // This panel shows the number of records per phylum, comparing against a reference list of phyla to identify any taxonomic gaps.
    // It helps to understand the diversity of taxa represented in the MPA records.
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
    // This section retrieves the occurrence records for the MPA and prepares them for mapping. It includes the ID, species name, phylum, event date, latitude, longitude, institution code, and whether the record is from the national node.
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
    /// The response object encapsulates all the data needed for the dashboard, including MPA metadata, record counts, institution contributions, temporal gaps, taxonomic gaps, and occurrence points. This object is cached for 30 minutes to improve performance on subsequent requests.
    var response = new MpaResponse(mpa, totalRecords, natRecords, institutions, yearSeries, taxonSeries, points);
    cache.Set(cacheKey, response, TimeSpan.FromMinutes(30));
    return Results.Ok(response);
});
// This endpoint generates a CSV file containing the occurrence records for the specified MPA. It retrieves the data from the cache and formats it into a CSV string, which is then returned as a downloadable file.
// If the MPA data is not found in the cache, it returns a bad request response, prompting the user to load the MPA in the dashboard first.
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
/// This method fetches JSON data from the OBIS API for a given URL. It uses an HttpClient to send a GET request and parses the response into a JsonNode. If the request fails or the response is not successful, it returns null. 
/// This method is used to retrieve various statistics and occurrence data for the defined MPAs.
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
// Define record types for MPA metadata and response structure. These records encapsulate the necessary information for each MPA and the aggregated data returned by the API endpoints.
public record MpaDef(string Id, string Name, string Designation, string Wkt, double Lat, double Lon);

// The MpaResponse record encapsulates the response structure for the MPA data, including metadata, record counts, institution contributions, temporal gaps, taxonomic gaps, and occurrence points. This structure is used to return comprehensive data for the dashboard.
public record MpaResponse(MpaDef Mpa, long TotalRecords, int NationalRecords, object Institutions, object Years, object Taxa, IEnumerable<dynamic> Points);
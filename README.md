# MPA Data Explorer — IOC-UNESCO OBIS Prototype

A minimal, containerised ASP.NET Core 8 and Leaflet/Chart.js web application for exploring marine biodiversity occurrences, national OBIS node contributions, publishing institutions, and temporal/taxonomic monitoring gaps inside Marine Protected Areas (MPAs).

## 1. How to Run It
Clone the repository, build the image, and run the container (requires Docker):

```bash
git clone https://github.com/bunyas/MpaDataExplorer.git
cd MpaDataExplorer
docker build -t mpa-data-explorer:1.0 .
docker run --rm -p 8080:8080 mpa-data-explorer:1.0
```

*(Note: If cloning places the project files inside a nested `MpaDataExplorer/MpaDataExplorer` subfolder, run `cd MpaDataExplorer` once more so you are in the directory containing `Dockerfile` and `MpaDataExplorer.csproj` before running `docker build`.)*

Open **`http://localhost:8080`** in your browser. No paid licence, external account, database setup, or manual step is required.

## 2. What Was Built
* **Pre-Loaded MPAs:** Three real MPAs in the Brazilian EEZ (*Parque Nacional Marinho dos Abrolhos*, *APA Costa dos Corais*, and *Fernando de Noronha*) serving as a real-data proxy for Costa Verde, with **OBIS Brazil** (`4bf79a01-65a9-4db6-b37b-18434f26ddfc`) configured as the National Node.
* **Responsive Map & Concurrent OBIS v3 Aggregation:** To stay responsive under latency constraints, the C# Minimal API executes three concurrent requests (`Task.WhenAll`) to OBIS v3 (`/v3/facet`, `/v3/statistics/years`, and a capped `/v3/occurrence?size=500` sample) and caches results in memory (`IMemoryCache`) for 30 minutes. Occurrences render on an HTML5 Canvas layer (`preferCanvas: true` in Leaflet), color-coded by National Node (green) vs. External Node (blue).
* **Three Gap-Aware Analytical Panels:**
  1. **(a) Contribution by OBIS Node & Publishing Institution:** Displays the National Node percentage share, top contributing institutions (`institutionCode`), and explicitly quantifies records lacking an institution as **"Unrecorded (Metadata Gap)"** in red.
  2. **(b) Records per Year (2010–2024):** Joins OBIS annual counts against a continuous 15-year axis (`Enumerable.Range(2010, 15)`). Unsampled (`0`-record) years are injected and highlighted in **red**.
  3. **(c) Records per Higher Taxon:** Evaluates observed phyla against 9 reference marine phyla (*Mollusca*, *Arthropoda*, *Chordata*, *Cnidaria*, *Echinodermata*, *Annelida*, *Porifera*, *Rhodophyta*, *Chlorophyta*). Phyla with `0` records are flagged and rendered in **red**.
* **CSV Export with Citation:** Streams the underlying occurrence table as UTF-8 CSV with an OBIS citation header on row 1.

## 3. Containerisation Architecture & Deployment Decisions
* **Base-Image Choice:** Uses a two-stage build (`[mcr.microsoft.com/dotnet/sdk:8.0.403-alpine3.20](https://mcr.microsoft.com/dotnet/sdk:8.0.403-alpine3.20)` -> `[mcr.microsoft.com/dotnet/aspnet:8.0.10-alpine3.20](https://mcr.microsoft.com/dotnet/aspnet:8.0.10-alpine3.20)`). Alpine Linux provides a minimal OS footprint, reduces the CVE attack surface, and runs as the built-in non-root user (`USER app`, UID `1654`).
* **How Dependencies Are Pinned:** Docker base images are pinned to exact `.NET 8` patch and Alpine OS versions (`8.0.403-alpine3.20` and `8.0.10-alpine3.20`). The C# backend uses **zero third-party NuGet packages** (relying solely on the .NET 8 Base Class Library), and frontend CDN assets (`Leaflet 1.9.4`, `Chart.js 4.4.1`) are pinned to immutable semantic versions.
* **Build-Time & Image-Size Trade-offs Accepted:** Multi-stage separation discards the ~850 MB SDK after `dotnet publish -c Release /p:UseAppHost=false`, producing a **~105 MB** runtime image. Copying `MpaDataExplorer.csproj` before source files caches `dotnet restore` across builds. We accepted standard JIT compilation over Native AOT (`PublishAot=true`) because AOT adds ~2 minutes of native C++ linker time and extra Alpine build packages (`clang`, `zlib-dev`) for negligible gain in an I/O-bound web proxy.
* **Configuration & Secrets in a Real Deployment:** Non-sensitive runtime settings (`ASPNETCORE_URLS`, `OBIS_API_BASE_URL`) are injected via environment variables bound through `builder.Configuration` (managed via Kubernetes `ConfigMap`s or Docker Compose environment files in production). Sensitive secrets (such as database connection strings, API keys, or TLS certificates) are never hardcoded or baked into the image; in production they would be mounted at runtime from Kubernetes Secrets, Docker Swarm Secrets, or a cloud vault (e.g., Azure Key Vault / HashiCorp Vault).

## 4. What Was Deliberately Not Built & Why
* **Full Client-Side Pagination of Raw Occurrences:** Downloading 50,000+ raw occurrence geometries into the browser causes multi-second freezes; server-side OBIS facets provide 100% accurate summary charts in ~150 ms while keeping the map payload lightweight.
* **Live WFS Polygon Streaming:** Fetching high-resolution WDPA polygons at runtime adds external failure points; pre-loaded WKT boundaries keep startup instantaneous.

## 5. Known Limitations
* **Sampled Map & CSV Payload:** The map and CSV export cap raw occurrences at 500 records per MPA for responsiveness, whereas KPIs and charts reflect 100% of OBIS records.
* **Simplified Bounding-Box Polygons:** WKT geometries are simplified so `GET` requests to `api.obis.org` do not exceed HTTP URI length limits (`414 URI Too Long`).
* **Process-Local Cache:** `IMemoryCache` is per-container and requires the MPA to be loaded in the UI before exporting its CSV.

## 6. What I Would Do With Two More Days
1. **PostGIS Backend (Question 4 Schema):** Replace live HTTP calls with a PostgreSQL/PostGIS database indexed with `GIST (geom)` to query complex `MULTIPOLYGON` WDPA boundaries without URI length limits.
2. **H3 Hexagonal Grid Tiles (`/v3/occurrence/grid`):** Render aggregated spatial density hex-bins at low zoom levels and switch to individual points when zoomed in.
3. **Resilience & Automated Tests:** Add `Microsoft.Extensions.Http.Resilience` retry/circuit-breaker policies and xUnit `WebApplicationFactory` integration tests.
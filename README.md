# MPA Data Explorer (IOC-UNESCO OBIS Prototype)

A minimal, containerised ASP.NET Core 8 and Leaflet/Chart.js web application for exploring marine biodiversity occurrences, national OBIS node contributions, publishing institutions, and temporal/taxonomic monitoring gaps inside Marine Protected Areas (MPAs).

## 1. How to Build and Run
Clone the repository and run the following commands from the project directory (requires Docker):

```bash
docker build -t mpa-data-explorer:1.0 .
docker run --rm -p 8080:8080 mpa-data-explorer:1.0
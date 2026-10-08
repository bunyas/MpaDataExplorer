# MPA Data Explorer — IOC-UNESCO OBIS Prototype

A minimal, containerised ASP.NET Core 8 and Leaflet/Chart.js web application for exploring marine biodiversity occurrences, national OBIS node contributions, publishing institutions, and temporal/taxonomic monitoring gaps inside Marine Protected Areas (MPAs).

## 1. How to Run It 
Clone the repository, build the image, and run the container (requires Docker):

```bash
git clone [https://github.com/bunyas/MpaDataExplorer.git](https://github.com/bunyas/MpaDataExplorer.git)
cd MpaDataExplorer
docker build -t mpa-data-explorer:1.0 .
docker run --rm -p 8080:8080 mpa-data-explorer:1.0
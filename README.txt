# Movie Recommendation System 🎬

AI-powered movie recommendation system using embeddings and semantic search.

## Tech Stack
- ASP.NET Core 9.0
- Entity Framework Core
- Ollama (nomic-embed-text model)
- Docker
- SQLite

## Quick Start

### Prerequisites
- .NET 8.0 SDK
- Ollama (running on http://localhost:11434)
- Docker (optional)

### Run Locally
```bash

# 0. Install Ollama
winget install Ollama.Ollama

# 1. Start Ollama
ollama serve

# 2. Pull embedding model
ollama pull nomic-embed-text
ollama pull all-minilm
ollama pull bge-m3

# 3. Run docker container
1) download docker-compose.prod.yml file from github
2) cd yourforlder with  docker-compose.prod.yml file 
3) docker-compose -f docker-compose.prod.yml up -d

TEST IN POSTMAN:
http://localhost:8080/api/recommendations/similar
{
  "description": "bond"
}

TRY IN UI:
Run Movies.html file in FrontEnd folder
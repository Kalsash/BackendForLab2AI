FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS base
WORKDIR /app
EXPOSE 8080
EXPOSE 8081

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY ["BackendForLab2AI.csproj", "."]
RUN dotnet restore "BackendForLab2AI.csproj"
COPY . .
RUN dotnet build "BackendForLab2AI.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "BackendForLab2AI.csproj" -c Release -o /app/publish

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "BackendForLab2AI.dll"]
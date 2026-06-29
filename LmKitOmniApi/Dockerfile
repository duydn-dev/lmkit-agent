# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY ["LmKitOmniApi/LmKitOmniApi.csproj", "LmKitOmniApi/"]
RUN dotnet restore "LmKitOmniApi/LmKitOmniApi.csproj"
COPY . .
WORKDIR "/src/LmKitOmniApi"
RUN dotnet build "LmKitOmniApi.csproj" -c Release -o /app/build
RUN dotnet publish "LmKitOmniApi.csproj" -c Release -o /app/publish

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app/publish .

# Health check để Docker/K8s kiểm tra trạng thái
HEALTHCHECK --interval=30s --timeout=5s --retries=3 \
    CMD curl -f http://localhost:5000/health || exit 1

ENV ASPNETCORE_URLS=http://+:5000
EXPOSE 5000
ENTRYPOINT ["dotnet", "LmKitOmniApi.dll"]

FROM mcr.microsoft.com/dotnet/runtime:9.0 AS base

# Install native dependencies for GameNetworkingSockets
RUN apt-get update && apt-get install -y \
    libprotobuf-dev \
    libssl3 \
    ca-certificates \
    && rm -rf /var/lib/apt/lists/*

# Set library path
ENV LD_LIBRARY_PATH="/usr/lib:$LD_LIBRARY_PATH"

USER $APP_UID
WORKDIR /app

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["Touhou99Relay.csproj", "./"]
RUN dotnet restore "Touhou99Relay.csproj"
COPY . .
WORKDIR "/src/"
RUN dotnet build "./Touhou99Relay.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./Touhou99Relay.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
EXPOSE 50295
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "Touhou99Relay.dll"]

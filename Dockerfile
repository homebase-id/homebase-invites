FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
LABEL org.opencontainers.image.source="https://github.com/homebase-id/raven-hosting"
ARG TARGETARCH

WORKDIR /build

COPY global.json ./
COPY src/Raven.Hosting/Raven.Hosting.csproj src/Raven.Hosting/
RUN dotnet restore -a $TARGETARCH src/Raven.Hosting/Raven.Hosting.csproj

COPY src/Raven.Hosting/ src/Raven.Hosting/
RUN dotnet publish --no-restore --warnaserror -a $TARGETARCH --self-contained false \
    src/Raven.Hosting/Raven.Hosting.csproj -c Release -o out

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime

RUN apt-get update -q && \
    apt-get install -yq curl && \
    apt-get clean && \
    rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=build /build/out .

# The database lives here; mount a volume on it
VOLUME /data
ENV RavenHosting__DatabasePath=/data/invitation-codes.db

EXPOSE 8080

ENTRYPOINT ["dotnet", "Raven.Hosting.dll"]

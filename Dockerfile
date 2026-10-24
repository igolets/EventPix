FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/EventPix.Web/EventPix.Web.csproj -c Release -o /out

FROM mcr.microsoft.com/dotnet/aspnet:10.0
# ffmpeg is invoked as an external process for video thumbnails (not linked).
RUN apt-get update && apt-get install -y --no-install-recommends ffmpeg \
    && rm -rf /var/lib/apt/lists/* \
    && mkdir -p /data /media /themes && chown -R $APP_UID /data /media /themes
WORKDIR /app
COPY --from=build /out .
ENV EventPix__DataPath=/data \
    EventPix__StoragePath=/media \
    EventPix__ThemesPath=/app/themes \
    ASPNETCORE_URLS=http://+:8080
VOLUME ["/data", "/media"]
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "EventPix.Web.dll"]

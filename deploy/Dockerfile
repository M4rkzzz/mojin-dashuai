FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY api/ ./
USER $APP_UID
ENTRYPOINT ["dotnet", "Hub.Api.dll"]

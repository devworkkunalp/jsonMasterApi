# Build Stage
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /app

# Copy csproj and restore as distinct layers
COPY *.csproj ./
RUN dotnet restore

# Copy everything else and build
COPY . ./
RUN dotnet publish -c Release -o out

# Runtime Stage
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app/out .

# Create reports directory for Smart Compare file logs
RUN mkdir -p wwwroot/reports && chmod 777 wwwroot/reports

# Render sets the PORT environment variable
ENV ASPNETCORE_URLS=http://+:5007
EXPOSE 5007

ENTRYPOINT ["dotnet", "JsonMaster.Api.dll"]

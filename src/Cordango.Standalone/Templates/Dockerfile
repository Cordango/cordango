# {{AppName}} — one image serving the API and the single-page app from the same origin.
#
# Same origin is a decision, not an accident. It is what lets the session live in a cookie with no
# CORS configuration to get wrong, and what lets the antiforgery token be read by this application's
# own script and by nothing else. Splitting the two across origins is possible and costs you both.

# ---- the front end -------------------------------------------------------------------------
FROM node:22-alpine AS web
WORKDIR /src/web

# Dependencies first, so that editing a component does not re-run the install.
#
# `npm ci` when there is a lock file, because it installs exactly what the lock file says and fails
# if the two have drifted — which is what you want from a build you expect to reproduce. A freshly
# generated application has no lock file yet: a lock file records a resolution against the registry,
# and the generator does not go to the network. Run `npm install` once in web/ and commit the result,
# and every build after that is the reproducible one.
COPY web/package.json web/package-lock.json* ./
RUN if [ -f package-lock.json ]; then npm ci; else npm install; fi

COPY web/ ./
RUN npm run build

# ---- the back end --------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS api
WORKDIR /src

# Project files first, then restore, then the rest of the source. Editing a controller then only
# re-runs the compile; it does not re-resolve every package.
COPY api/*.csproj ./api/
{{RuntimeProjectCopy}}RUN dotnet restore ./api

{{RuntimeSourceCopy}}COPY api/ ./api/
COPY --from=web /src/api/wwwroot ./api/wwwroot
RUN dotnet publish ./api -c Release -o /app --no-restore

# ---- what actually ships -------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app

# curl, for the health check below. The aspnet image is slim and ships neither curl nor wget, so a
# HEALTHCHECK written against one of them reports "unhealthy" forever while the application is
# perfectly fine — a failure that looks exactly like a broken application.
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*

# The image already ships a non-root `app` user, and creating a second one fails the build. All this
# needs is somewhere writable for uploads: a web process has no business being able to write to its
# own binaries.
RUN mkdir -p /appdata/media && chown -R app /appdata
USER app

COPY --from=api /app .

ENV ASPNETCORE_HTTP_PORTS=8080 \
    Storage__Path=/appdata/media
EXPOSE 8080

# The endpoint asks the database, so an instance that cannot answer a query reports unhealthy rather
# than merely alive. The start period covers the first migration against an empty database.
HEALTHCHECK --interval=30s --timeout=3s --start-period=40s \
    CMD curl -fsS http://localhost:8080/health || exit 1

ENTRYPOINT ["dotnet", "{{AppNamespace}}.Api.dll"]

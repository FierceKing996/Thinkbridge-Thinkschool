# Single-container deploy for QuotesApi + quotes-ui.
#
# Why one image, not two services: QuotesApi's own Program.cs already serves
# the built Angular app as static files and falls back to index.html for any
# unmatched route (see day1/QuotesApi/Program.cs's UseStaticFiles()/
# MapFallbackToFile("index.html") - both wired in specifically so the SPA's
# client-side routes resolve on a hard refresh). That's the same-origin
# design this app was already built around (no CORS config anywhere, no
# proxy config in quotes-ui) - splitting it into two deployed services would
# fight that, not follow it. One Dockerfile building both halves and copying
# the Angular build's output into wwwroot/ before publish is what keeps a
# live deploy behaving exactly like `dotnet run` + `ng serve` locally.
#
# Build context is the REPO ROOT (not day1/QuotesApi/) - this file needs to
# COPY from both quotes-ui/ and day1/QuotesApi/, and Docker COPY paths are
# always relative to the build context, never to the Dockerfile's own
# location. On Render, that means dockerContext: . in render.yaml even
# though this Dockerfile happens to live at the repo root too.

# ---- Stage 1: build the Angular frontend -----------------------------------
FROM node:22-alpine AS ui-build
WORKDIR /ui

# Separate copy+install layer so `npm ci` is only re-run when the lockfile
# actually changes, not on every source edit.
COPY quotes-ui/package.json quotes-ui/package-lock.json ./
RUN npm ci

COPY quotes-ui/ ./
# Production config - same command CI/local release builds would use.
# Outputs to dist/quotes-ui/browser/ (the new Angular application builder's
# layout, confirmed against the real dist/ produced by `ng build` here).
RUN npx ng build --configuration production

# ---- Stage 2: build/publish the .NET backend --------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS api-build
WORKDIR /src

# Restore first, from just the project file, so dependency restore is its
# own cached layer independent of source changes.
COPY day1/QuotesApi/QuotesApi.csproj day1/QuotesApi/
RUN dotnet restore day1/QuotesApi/QuotesApi.csproj

COPY day1/QuotesApi/ day1/QuotesApi/
RUN dotnet publish day1/QuotesApi/QuotesApi.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish

# ---- Stage 3: runtime ---------------------------------------------------
# Alpine, matching QuotesApi.csproj's own <ContainerBaseImage> choice for the
# SDK-container-support publish path this project also supports (dotnet
# publish -t:PublishContainer) - same base image either way.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS final
WORKDIR /app

RUN apk add --no-cache icu-libs
ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false

COPY --from=api-build /app/publish .
# The Angular build's output becomes QuotesApi's wwwroot/ - overwriting
# whatever placeholder is baked into the publish output (Program.cs's
# comment on wwwroot/.gitkeep explains why an empty wwwroot/ is kept in the
# repo at all: so this directory always exists to copy into).
COPY --from=ui-build /ui/dist/quotes-ui/browser ./wwwroot

# Render (and most container hosts) inject PORT at runtime and expect the
# process to bind to it - it is not knowable at build time, so it can't be
# baked in via ENV ASPNETCORE_URLS here. This entrypoint script resolves it
# at container start instead, falling back to 8080 (the official ASP.NET
# Core Alpine image's own default HTTP port from .NET 8 onward) for any
# other host that doesn't set PORT at all.
RUN printf '#!/bin/sh\nset -e\nexec dotnet QuotesApi.dll --urls "http://0.0.0.0:${PORT:-8080}"\n' > /app/entrypoint.sh \
    && chmod +x /app/entrypoint.sh

EXPOSE 8080
ENTRYPOINT ["/app/entrypoint.sh"]

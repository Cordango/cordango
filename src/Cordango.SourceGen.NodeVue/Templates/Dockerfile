# {{AppName}} — one image: the API and the front end it serves.
#
# Two builds and one runtime stage. The front end is built first because it produces static files
# the API stage copies in; the API is compiled from TypeScript; and what ships is neither
# toolchain — only Node, the production dependencies and the compiled output.

# ---- the front end --------------------------------------------------------------------------

FROM node:22-alpine AS web

WORKDIR /src/web

# The manifest before the source, so a change to a component does not re-run `npm ci`. Layer order
# is the whole of Docker's build cache and this is where the minutes are.
COPY web/package.json web/package-lock.json* ./
RUN npm ci --no-audit --no-fund || npm install --no-audit --no-fund

COPY web/ ./
RUN npm run build

# ---- the API --------------------------------------------------------------------------------

FROM node:22-alpine AS api

WORKDIR /src/api

COPY api/package.json api/package-lock.json* ./
RUN npm ci --no-audit --no-fund || npm install --no-audit --no-fund

COPY api/ ./
RUN npm run build

# Production dependencies only, into a clean tree. `npm ci --omit=dev` after the build rather than
# before it: the build needs TypeScript, and what ships must not.
RUN npm ci --omit=dev --no-audit --no-fund || npm install --omit=dev --no-audit --no-fund

# ---- what ships -----------------------------------------------------------------------------

FROM node:22-alpine AS final

# Not root. The application writes to appdata and nothing else, and an image that runs as root is
# one container escape away from being a host compromise.
RUN addgroup -S app && adduser -S -G app app

WORKDIR /app

COPY --from=api --chown=app:app /src/api/node_modules ./node_modules
COPY --from=api --chown=app:app /src/api/dist ./dist
COPY --from=api --chown=app:app /src/api/package.json ./package.json

# The built single-page app, where the server expects it. Same origin as the API, which is what
# makes the session cookie work with no CORS.
COPY --from=web --chown=app:app /src/api/public ./public

# Uploads and, without DATABASE_URL, the database itself. A named volume in docker-compose.yml, so
# a rebuild does not take the data with it.
RUN mkdir -p /appdata && chown -R app:app /appdata

USER app

ENV NODE_ENV=production
ENV PORT=8080
ENV WEB_ROOT=/app/public
ENV STORAGE_PATH=/appdata/media

EXPOSE 8080

# No shell form: a `CMD node ...` string runs under /bin/sh, which does not forward SIGTERM to the
# process it spawned — so a `docker stop` would wait the full timeout and then kill the application
# mid-write rather than letting it close its pool.
CMD ["node", "dist/server.js"]

FROM node:22-alpine AS build
WORKDIR /app

COPY package.json ./
RUN npm install

COPY . .
ARG VITE_API_URL=http://localhost:8080
ENV VITE_API_URL=$VITE_API_URL
RUN npm run build

FROM nginx:alpine AS final
COPY --from=build /app/dist /usr/share/nginx/html

RUN printf 'server {\n\
    listen 3000;\n\
    server_name _;\n\
    root /usr/share/nginx/html;\n\
    index index.html;\n\
    location / {\n\
        try_files $uri $uri/ /index.html;\n\
    }\n\
}\n' > /etc/nginx/conf.d/default.conf

RUN printf '#!/bin/sh\n\
API_TARGET="${VITE_API_URL:-${VITE_BACKEND_URL:-${BACKEND_URL:-${API_URL:-http://localhost:8080}}}}"\n\
echo "window.__APP_CONFIG__ = { API_URL: \\"${API_TARGET}\\" };" > /usr/share/nginx/html/config.js\n\
exec nginx -g "daemon off;"\n' > /docker-entrypoint-custom.sh && chmod +x /docker-entrypoint-custom.sh

EXPOSE 3000
ENTRYPOINT ["/docker-entrypoint-custom.sh"]


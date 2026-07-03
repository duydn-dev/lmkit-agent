# Build stage
FROM node:20-alpine AS build-stage
WORKDIR /app

# Install dependencies first for caching
COPY package*.json ./
RUN npm install

# Copy all files and build
COPY . .
RUN npm run build

# Production stage
FROM nginx:alpine AS production-stage
COPY --from=build-stage /app/dist /usr/share/nginx/html

# Expose port 80
EXPOSE 80

# Start Nginx
CMD ["nginx", "-g", "daemon off;"]

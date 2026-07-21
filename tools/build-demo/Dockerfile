# A tiny multi-step image to exercise the Build flow (BuildKit stages + cache hits).
FROM alpine:3.20 AS base
RUN apk add --no-cache ca-certificates

FROM base AS build
WORKDIR /app
COPY hello.sh .
RUN chmod +x hello.sh

FROM base
WORKDIR /app
COPY --from=build /app/hello.sh .
CMD ["./hello.sh"]

# The mkwmp server (`mkwmp server` + `mkwmp lobby`), built from this checkout.
# Used by compose.yaml at the repo root; see docs/HOSTING.md.

FROM rust:1-bookworm AS build
WORKDIR /src
COPY rust/ rust/
COPY contracts/ contracts/
# One fixed cache id for the registry and one for the build dir.
RUN --mount=type=cache,id=mkwmp-cargo-registry,target=/usr/local/cargo/registry \
    --mount=type=cache,id=mkwmp-cargo-target,target=/src/rust/target \
    cargo build --release --locked --manifest-path rust/Cargo.toml -p mkwmp \
 && cp rust/target/release/mkwmp /usr/local/bin/mkwmp

FROM debian:bookworm-slim
RUN apt-get update \
 && apt-get install -y --no-install-recommends ca-certificates \
 && rm -rf /var/lib/apt/lists/* \
 && useradd --system --uid 10001 --home-dir /data mkwmp \
 && install -d -o mkwmp -g mkwmp /data
COPY --from=build /usr/local/bin/mkwmp /usr/local/bin/mkwmp
USER mkwmp
WORKDIR /data
VOLUME /data
# First start creates keys, certificate, config and registry in /data;
# settings come from MKWMP_* (see .env.example).
ENV MKWMP_DATA=/data
CMD ["mkwmp", "host"]

FROM debian:bookworm-slim

ARG JEV_VERSION=jevos-v4
RUN apt-get update && apt-get install -y --no-install-recommends ca-certificates curl unzip \
    && rm -rf /var/lib/apt/lists/* \
    && curl -fsSL "https://github.com/feder-cr/jev/releases/download/${JEV_VERSION}/jev-linux-x64.tar.gz" -o /tmp/jev.tar.gz \
    && tar -xzf /tmp/jev.tar.gz -C /opt \
    && curl -fsSL "https://github.com/feder-cr/jev/releases/download/${JEV_VERSION}/${JEV_VERSION}-openvino-int8.zip" -o /tmp/model.zip \
    && unzip /tmp/model.zip -d /opt/jev \
    && rm /tmp/jev.tar.gz /tmp/model.zip

WORKDIR /opt/jev
EXPOSE 8017
ENTRYPOINT ["./jev", "serve", "--host", "0.0.0.0", "--port", "8017", "--threads", "2"]

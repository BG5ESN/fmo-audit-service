# FMO审计工具 — Docker 镜像
# 基础镜像选 glibc 系（Debian/Ubuntu）：publish 的 linux-x64 单文件是 glibc 构建，
# 不要用 alpine（musl 不兼容）
FROM debian:bookworm-slim

# 单文件二进制（由 script/build-all.sh 生成，放到本目录或指定路径）
COPY emqx-monitor-server /app/emqx-monitor-server
RUN chmod +x /app/emqx-monitor-server

# 数据目录（db）与 Data Protection 密钥目录都持久化
ENV EMQX_MONITOR_PORT=9527
ENV EMQX_MONITOR_DB=/data/emqx-monitor-server.db
# 密钥目录固定（ASP.NET Core Data Protection 默认读 HOME/.aspnet）
ENV HOME=/data

EXPOSE 9527

# 非 root 运行（db 含 EMQX Secret / ingest token，禁止 root）
RUN useradd -r -d /data -s /usr/sbin/nologin fmo-audit && mkdir -p /data && chown fmo-audit /data
USER fmo-audit

ENTRYPOINT ["/app/emqx-monitor-server"]

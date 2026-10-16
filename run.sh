#!/usr/bin/env bash
# API Monitor 一键部署：构建镜像并以常驻容器运行（数据持久化在宿主机）
# 用法：bash run.sh [端口]     环境变量：DATA_DIR（默认 /root/api-monitor-data）
set -euo pipefail
cd "$(dirname "$0")"

PORT="${1:-${PORT:-5240}}"
NAME="api-monitor"
IMAGE="api-monitor:latest"
DATA_DIR="${DATA_DIR:-/root/api-monitor-data}"
mkdir -p "$DATA_DIR"

# 首次运行生成配置：加密密钥随机生成后不再变化，否则已保存的 API Key 将无法解密
if [ ! -f "$DATA_DIR/appsettings.json" ]; then
  SECRET=$(openssl rand -hex 32 2>/dev/null || head -c 32 /dev/urandom | od -An -tx1 | tr -d ' \n')
  cat > "$DATA_DIR/appsettings.json" <<EOF
{
  "ConnectionStrings": { "Default": "Data Source=/data/api-monitor.db" },
  "Database": { "Provider": "sqlite" },
  "Security": { "JwtSecret": "replace-with-a-long-random-secret-at-least-32-chars", "KeyEncryptionSecret": "$SECRET" },
  "Wanjitong": { "Endpoint": "https://www.wanjitong.cn/api/memos" },
  "Logging": { "LogLevel": { "Default": "Information", "Microsoft.AspNetCore": "Warning" } },
  "AllowedHosts": "*"
}
EOF
  echo "已生成 $DATA_DIR/appsettings.json"
  echo "  - KeyEncryptionSecret 已随机生成，后续请勿修改（否则已保存的密钥无法解密）"
  echo "  - 如需邮件通知，请在该文件补充 Smtp 节后重新执行本脚本"
fi

echo "==> 构建镜像 $IMAGE ..."
docker build -t "$IMAGE" .

docker rm -f "$NAME" >/dev/null 2>&1 || true
mkdir -p "$DATA_DIR/dataprotection"
echo "==> 启动容器（端口 $PORT，数据目录 $DATA_DIR）..."
docker run -d --name "$NAME" --restart unless-stopped \
  -p "$PORT":8080 \
  -v "$DATA_DIR":/data \
  -v "$DATA_DIR/appsettings.json":/app/appsettings.json:ro \
  -v "$DATA_DIR/dataprotection":/root/.aspnet/DataProtection-Keys \
  "$IMAGE" >/dev/null

sleep 3
docker ps --filter "name=$NAME" --format 'table {{.Names}}\t{{.Status}}\t{{.Ports}}'
echo "==> 部署完成: http://0.0.0.0:$PORT  （日志: docker logs -f $NAME）"

#!/usr/bin/env bash
# 构建 RemoteCI 飞牛 fnOS 应用包（.fpk）。
#
# 用法：
#   ./fnos/build.sh [版本] [fnpack 路径]
#
# 版本缺省从 REMOTECI_VERSION 或最近的 git tag 读取，均无时回退到
# server/RemoteCI.Server/RemoteCI.Server.csproj 的 <Version>（本地默认版本唯一来源）。
# fnpack 路径缺省为 .tools/fnpack；可通过 FNPACK 环境变量或第二个参数指定。
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
FNOS_DIR="$ROOT/fnos"

LOCAL_FALLBACK="$(sed -n 's/.*<Version>\([^<]*\)<\/Version>.*/\1/p' \
  "$ROOT/server/RemoteCI.Server/RemoteCI.Server.csproj" | head -n 1)"
VERSION="${1:-${REMOTECI_VERSION:-$(git -C "$ROOT" describe --tags --abbrev=0 2>/dev/null || echo "${LOCAL_FALLBACK:-0.0.0}")}}"
VERSION="${VERSION#v}"
FNPACK="${2:-${FNPACK:-$ROOT/.tools/fnpack}}"

if [ ! -x "$FNPACK" ]; then
  echo "fnpack 不存在或不可执行: $FNPACK" >&2
  echo "请先下载 https://developer.fnnas.com/docs/cli/fnpack/ 中的 linux-amd64 版本到 .tools/fnpack 并 chmod +x。" >&2
  exit 1
fi

STAGE="$(mktemp -d)"
trap 'rm -rf "$STAGE"' EXIT

cp -R "$FNOS_DIR/." "$STAGE/"
sed -i "s/__REMOTECI_VERSION__/$VERSION/g" \
  "$STAGE/manifest" \
  "$STAGE/app/docker/docker-compose.yaml"

mkdir -p "$ROOT/artifacts/release"
cd "$STAGE"
"$FNPACK" build

# fnpack 把产物写到工作目录；find 兜底定位，避免不同版本输出位置差异。
FPK="$(find "$STAGE" -maxdepth 1 -name '*.fpk' | head -n 1)"
if [ -z "$FPK" ]; then
  echo "fnpack 构建成功但未找到 fpk 产物，请检查打包目录。" >&2
  exit 1
fi
mv "$FPK" "$ROOT/artifacts/release/RemoteCI-$VERSION.fpk"

echo "已生成: artifacts/release/RemoteCI-$VERSION.fpk"

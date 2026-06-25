#!/usr/bin/env bash
# explain.sh — прогон EXPLAIN (ANALYZE, TIMING, BUFFERS) по одному/нескольким запросам
# и краткая сводка (время планирования/выполнения, тип сканов). Часть набора tools/.
#
# Usage:
#   tools/dbq/explain.sh "SQL1" ["SQL2" ...]
#   DB=DirRX261OGVGenAI tools/dbq/explain.sh "select ..."
#
# Требует .NET 10 (по умолчанию C:/dotnet10/dotnet.exe, переопределяется DOTNET=).
set -u
DOTNET="${DOTNET:-C:/dotnet10/dotnet.exe}"
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
DBARG=(); [ -n "${DB:-}" ] && DBARG=(--db "$DB")
i=0
for q in "$@"; do
  i=$((i+1))
  echo "===== [$i] ====="
  "$DOTNET" run --project "$HERE" -- "explain (analyze, timing, buffers) $q" "${DBARG[@]}" 2>&1 \
    | grep -iE "Execution Time|Planning Time|Seq Scan|Index .*Scan|Bitmap|Gather|HashAggregate|Sort " | head -10
  echo
done

#!/usr/bin/env bash
# Alle Prüfungen, die ohne Windows- oder macOS-Toolchain laufen.
#
# Die plattformgebundenen Tests (dotnet test, swift test) sind hier bewusst
# NICHT enthalten - sie brauchen die jeweilige Zielplattform. Siehe
# docs/06-build-and-run.md §6.4.
set -euo pipefail
cd "$(dirname "$0")"

echo "═══ Signaling-Server ═══"
(cd signaling && npm test 2>&1 | grep -E "^# (tests|pass|fail)")

echo
echo "═══ Krypto-Testvektoren ═══"
node tools/crypto-vectors/verify.mjs | tail -2

echo
echo "═══ Implementierungs-Konsistenz ═══"
python3 tools/consistency/check.py | tail -1

echo
echo "═══ JSON-Schemas ═══"
python3 tools/consistency/schema_check.py | tail -1

#!/bin/bash
# runs two copies of Mutual on this pc (profiles rehearsal-a and rehearsal-b, made by
# "Mutual.Lab rehearsal-setup"), has a share a box with b through the real app flow (request ping,
# auto-accept, hub link, udp lane, viewer window), then sends b a file. prints both status reports.
cd "$(dirname "$0")/.."
EXE=${EXE:-src/Mutual/bin/Debug/net8.0-windows10.0.19041.0/Mutual.exe}
T=$(mktemp -d)
powershell -NoProfile -Command "Get-Process Mutual -ErrorAction SilentlyContinue | Where-Object { \$_.Path -like '$(cygpath -w "$PWD")\*' } | Stop-Process -Force" >/dev/null 2>&1
"$EXE" --profile rehearsal-b --report "$T/b.json" ${BDO:+--do "$BDO"} &
sleep 2
"$EXE" --profile rehearsal-a --do "${DO:-stream-box 120 120 960 540}" ${STOP:+--stop-after $STOP} --report "$T/a.json" &
for i in $(seq 1 ${WAIT:-20}); do sleep 1; done
echo "a (sharing): $(cat $T/a.json)"
echo "b (watching): $(cat $T/b.json)"
R="$APPDATA/Mutual/profiles"
for p in rehearsal-a rehearsal-b; do echo "-- $p activity"; tail -6 "$R/$p/activity.jsonl" | python -c "import sys,json; [print('  ', json.loads(l)['result'], json.loads(l)['action']) for l in sys.stdin]"; done
[ -n "$KEEP" ] || powershell -NoProfile -Command "Get-Process Mutual -ErrorAction SilentlyContinue | Where-Object { \$_.Path -like '$(cygpath -w "$PWD")\*' } | Stop-Process -Force" >/dev/null 2>&1

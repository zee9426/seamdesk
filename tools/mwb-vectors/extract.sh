#!/usr/bin/env bash
# Copies Mouse Without Borders' own serialisation and encryption code, unchanged, from a PowerToys checkout into
# ./mwb/<format> (git-ignored). "old": the last commit before the 0.101 wire change (a9e1f0f20, "per-connection random
# salt and IV"). "current": 48c4bad07, which has every later wire change (a64c400b7, 100000 PBKDF2 iterations); both
# first shipped in v0.101.2211.0. Whole files where they compile alone; otherwise single members, cut from their
# declaration to the closing brace at the same indent. Nothing from MWB is committed to Seamdesk.
# Usage: tools/mwb-vectors/extract.sh <powertoys checkout> <old|current>
set -euo pipefail
PT=$1
case $2 in
  old) REV='a9e1f0f20^' ;;
  current) REV='48c4bad07' ;;
  *) echo "extract.sh: say old or current" >&2; exit 1 ;;
esac
APP=src/modules/MouseWithoutBorders/App
OUT=$(cd "$(dirname "$0")" && pwd)/mwb/$2
rm -rf "$OUT" && mkdir -p "$OUT"

show() { git -C "$PT" show "$REV:$APP/$1"; }

# member FILE REGEX: the first member whose declaration matches REGEX, through its closing brace at the same indent.
member() {
  local src text
  src=$(show "$1")
  text=$(PAT="$2" awk '
    !found && $0 ~ ENVIRON["PAT"] { found = 1; match($0, /^ */); ind = substr($0, 1, RLENGTH); print; if ($0 ~ /;[ \t\r]*$/) exit; next }
    found { print; if ($0 == ind "}" || $0 == ind "}\r") exit }' <<< "$src")
  [ -n "$text" ] || { echo "extract.sh: no member matching '$2' in $1" >&2; exit 1; }
  printf '%s\n' "$text"
}

for f in Core/Encryption.cs Core/DATA.cs Core/Package.cs Core/PackageType.cs Core/MOUSEDATA.cs Core/KEYBDDATA.cs \
         Core/ID.cs Core/ClipboardPostAction.cs Core/PackageMonitor.cs Core/MyRectangle.cs \
         Exceptions/ExpectedSocketException.cs Exceptions/KnownException.cs; do
  show "$f" > "$OUT/$(basename "$f")"
done

# The current Encryption.cs refuses a key stored in DPAPI form (IsKeyValid); that check is in these two.
if [ "$2" = current ]; then
  for f in Class/PairingKeyCodec.cs Class/IPairingKeyCodec.cs; do
    show "$f" > "$OUT/$(basename "$f")"
  done
fi

HEADER="// Extracted by tools/mwb-vectors/extract.sh from PowerToys $REV (MIT, Copyright (c) Microsoft Corporation)."

{
  echo "$HEADER"
  echo 'using System; using System.Drawing; using System.IO; using System.Net.Sockets; using System.Security.Cryptography; using System.Text; using System.Threading; using MouseWithoutBorders.Class;'
  echo 'namespace MouseWithoutBorders.Core;'
  echo 'internal static partial class Common'
  echo '{'
  member Core/Common.cs 'internal static byte\[\] GetBytes\('
  member Core/Common.cs 'internal static string GetString\('
  member Core/Common.cs 'internal static byte\[\] GetBytesU\('
  member Core/Common.cs 'internal static string GetStringU\('
  member Core/Common.cs 'internal static long GetTick\('
  member Core/Common.cs 'internal static void SendOrReceiveARandomDataBlockPerInitialIV\('
  member Core/Common.cs 'internal static void SendPackage\('
  member Core/Common.cs 'internal static void SendHeartBeat\('
  member Core/Common.cs 'private static long lastSendNextMachine;'
  member Core/Common.cs 'internal static void SendNextMachine\('
  member Core/Common.cs 'internal static void SendHello\('
  member Core/Common.cs 'internal static void SendByeBye\('
  echo '}'
  echo 'internal static partial class MachineStuff'
  echo '{'
  member Core/MachineStuff.cs 'private static Point ConvertToUniversalValue\('
  echo '}'
} > "$OUT/Common.Extracted.cs"

{
  echo "$HEADER"
  echo 'using System; using System.IO; using System.Net; using System.Net.Sockets; using System.Globalization; using MouseWithoutBorders.Core; using MouseWithoutBorders.Exceptions;'
  echo 'namespace MouseWithoutBorders.Class'
  echo '{'
  member Class/SocketStuff.cs 'internal enum SocketStatus'
  member Class/SocketStuff.cs 'internal class TcpSk'
  echo 'internal partial class SocketStuff'
  echo '{'
  member Class/SocketStuff.cs 'private static int TcpSendData\('
  member Class/SocketStuff.cs 'private static void ProcessReceivedDataEx\('
  member Class/SocketStuff.cs 'internal static DATA TcpReceiveData\('
  member Class/SocketStuff.cs 'private static void PreProcessData\('
  member Class/SocketStuff.cs 'internal int TcpSend\('
  echo '}'
  echo 'internal static partial class Extensions'
  echo '{'
  member Class/Extensions.cs 'internal static int ReadEx\('
  echo '}'
  echo '}'
} > "$OUT/SocketStuff.Extracted.cs"

{
  echo "$HEADER"
  echo 'using System; using System.IO; using System.IO.Compression; using System.Threading; using System.Threading.Tasks; using System.Globalization; using MouseWithoutBorders.Class;'
  echo 'namespace MouseWithoutBorders.Core;'
  echo 'internal static partial class Clipboard'
  echo '{'
  member Core/Clipboard.cs 'private static readonly char\[\] NullSeparator'
  member Core/Clipboard.cs 'private const int DATA_SIZE'
  member Core/Clipboard.cs 'private const string TEXT_TYPE_SEP'
  member Core/Clipboard.cs 'private static long lastClipboardEventTime;'
  member Core/Clipboard.cs 'private static long LastClipboardEventTime'
  member Core/Clipboard.cs 'private static void SendClipboardDataUsingTCP\('
  member Core/Clipboard.cs 'internal static void ReceiveClipboardDataUsingTCP\('
  member Core/Clipboard.cs 'private static void SetClipboardData\('
  echo '}'
} > "$OUT/Clipboard.Extracted.cs"

echo "extracted $(cat "$OUT"/*.cs | wc -l) lines of MWB code into $OUT"

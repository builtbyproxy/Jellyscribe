#!/bin/bash
set -e

SERVER="lachlan@192.168.1.122"
PLUGINS_ROOT="/docker/jellyfin/config/data/plugins"
PROJECT_DIR="$(cd "$(dirname "$0")" && pwd)"

read -s -p "Server password: " PASS
echo

echo "Building..."
dotnet build -c Release "$PROJECT_DIR/LetterboxdSync/LetterboxdSync.csproj" -q

# Jellyfin renames the plugin directory to Jellyscribe_<assemblyVersion> on first
# load, so the install path drifts every release. Discover it instead of hardcoding.
# (Was LetterboxdSync_* before the AssemblyName rebrand; check both once on the
# first post-rebrand deploy in case an existing install hasn't picked it up yet.)
echo "Resolving plugin directory on server..."
PLUGIN_DIR=$(sshpass -p "$PASS" ssh "$SERVER" \
    "ls -d $PLUGINS_ROOT/Jellyscribe_* $PLUGINS_ROOT/LetterboxdSync_* 2>/dev/null | sort -V | tail -1")
if [ -z "$PLUGIN_DIR" ]; then
    echo "Could not find Jellyscribe_* or LetterboxdSync_* under $PLUGINS_ROOT on the server."
    exit 1
fi
echo "Target: $PLUGIN_DIR"

echo "Deploying..."
sshpass -p "$PASS" scp \
    "$PROJECT_DIR/LetterboxdSync/bin/Release/net9.0/Jellyscribe.dll" \
    "$PROJECT_DIR/LetterboxdSync/bin/Release/net9.0/HtmlAgilityPack.dll" \
    "$SERVER:$PLUGIN_DIR/"

echo "Restarting Jellyfin..."
sshpass -p "$PASS" ssh "$SERVER" 'docker restart jellyfin'

echo "Done. Wait a few seconds for Jellyfin to start."

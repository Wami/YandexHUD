#!/usr/bin/env bash
# Dev helper for tools/MockRoute: install, build a route, start/stop playback.
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
package="com.avashield.mockroute"
service="$package/.MockRouteService"
route_file="$repo_root/artifacts/mockroute/route.json"
from="55.748519,38.003984"
to="55.735000,38.030000"
speed="60"
detour="0"
android_sdk="${ANDROID_HOME:-${ANDROID_SDK_ROOT:-$HOME/Android/Sdk}}"
java_sdk="${JAVA_HOME:-}"
mode=""

usage() {
    cat <<'EOF'
Usage: scripts/mock-route.sh <command> [options]

Commands:
  install              Build and install MockRoute, grant location and mock appops
  start                Generate a route, push it and start playback
  stop                 Stop playback
  navi                 Open the destination route in Yandex Navigator
  clean                Stop, revoke mock location and uninstall the helper

Options:
  --from LAT,LON       Route start (default 55.748519,38.003984)
  --to LAT,LON         Route destination (default 55.735000,38.030000)
  --speed KMH          Playback speed (default 60)
  --detour METERS      Side offset for 30-38% of the route (default 0)
  --android-sdk PATH   Android SDK for install (default ANDROID_HOME)
  --java-sdk PATH      JDK for install (default JAVA_HOME)
EOF
}

while [[ $# -gt 0 ]]; do
    case "$1" in
        install|start|stop|navi|clean) mode="$1" ;;
        --from) from="${2:-}"; shift ;;
        --to) to="${2:-}"; shift ;;
        --speed) speed="${2:-}"; shift ;;
        --detour) detour="${2:-}"; shift ;;
        --android-sdk) android_sdk="${2:-}"; shift ;;
        --java-sdk) java_sdk="${2:-}"; shift ;;
        -h|--help) usage; exit 0 ;;
        *) echo "Unknown option: $1" >&2; usage >&2; exit 2 ;;
    esac
    shift
done

[[ -n "$mode" ]] || { usage; exit 2; }

case "$mode" in
    install)
        [[ -d "$android_sdk" ]] || {
            echo "Pass --android-sdk or set ANDROID_HOME." >&2; exit 1;
        }
        [[ -d "$java_sdk" ]] || {
            echo "Pass --java-sdk or set JAVA_HOME." >&2; exit 1;
        }
        dotnet build "$repo_root/tools/MockRoute/MockRoute.csproj" \
            -c Debug -t:SignAndroidPackage \
            "-p:AndroidSdkDirectory=$android_sdk" \
            "-p:JavaSdkDirectory=$java_sdk"
        adb install --no-incremental -r \
            "$repo_root/tools/MockRoute/bin/Debug/net9.0-android/$package-Signed.apk"
        adb shell pm grant "$package" android.permission.ACCESS_FINE_LOCATION
        adb shell pm grant "$package" android.permission.ACCESS_COARSE_LOCATION
        adb shell appops set "$package" android:mock_location allow
        ;;
    start)
        mkdir -p "$(dirname "$route_file")"
        python3 "$repo_root/tools/MockRoute/make_route.py" \
            --from "$from" --to "$to" --speed-kmh "$speed" \
            --detour "$detour" --out "$route_file"
        adb shell mkdir -p "/sdcard/Android/data/$package/files"
        adb push "$route_file" "/sdcard/Android/data/$package/files/route.json"
        adb shell am start-foreground-service -n "$service" -a mockroute.START
        ;;
    stop)
        adb shell am startservice -n "$service" -a mockroute.STOP \
            || adb shell am force-stop "$package"
        ;;
    navi)
        lat="${to%%,*}"
        lon="${to#*,}"
        adb shell "am start -a android.intent.action.VIEW \
            -d 'yandexnavi://build_route_on_map?lat_to=$lat&lon_to=$lon'"
        ;;
    clean)
        adb shell am startservice -n "$service" -a mockroute.STOP || true
        adb shell am force-stop "$package" || true
        adb shell appops set "$package" android:mock_location deny || true
        adb uninstall "$package" || true
        ;;
esac

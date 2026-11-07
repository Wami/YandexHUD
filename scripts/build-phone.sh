#!/usr/bin/env bash
# Build the phone APK (com.avashield.yandexhud.phone) on Linux/macOS.
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
android_sdk="${ANDROID_HOME:-${ANDROID_SDK_ROOT:-$HOME/Android/Sdk}}"
java_sdk="${JAVA_HOME:-}"
signing_key=""
configuration="Release"
output_directory="$repo_root/artifacts/phone"
apk_name="YandexHUD-Phone-Navigator-1.4"

usage() {
    cat <<'EOF'
Usage: scripts/build-phone.sh [options]

Options:
  --android-sdk PATH   Android SDK with platform 35 (default: ANDROID_HOME)
  --java-sdk PATH      JDK 21 (default: JAVA_HOME)
  --signing-key PATH   Existing debug keystore to sign with
  --debug              Build a Debug APK with the local probe menu
  -h, --help           Show this help
EOF
}

while [[ $# -gt 0 ]]; do
    case "$1" in
        --android-sdk) android_sdk="${2:-}"; shift 2 ;;
        --java-sdk) java_sdk="${2:-}"; shift 2 ;;
        --signing-key) signing_key="${2:-}"; shift 2 ;;
        --debug) configuration="Debug"; output_directory="$repo_root/artifacts/phone-debug"; apk_name+="-debug"; shift ;;
        -h|--help) usage; exit 0 ;;
        *) echo "Unknown option: $1" >&2; usage >&2; exit 2 ;;
    esac
done

[[ -n "$android_sdk" && -d "$android_sdk" ]] || {
    echo "Pass --android-sdk or set ANDROID_HOME to Android SDK 35." >&2
    exit 1
}
[[ -n "$java_sdk" && -d "$java_sdk" ]] || {
    echo "Pass --java-sdk or set JAVA_HOME to a JDK supported by the installed Android workload (17 or 21; see XA0030)." >&2
    exit 1
}
if [[ -n "$signing_key" && ! -f "$signing_key" ]]; then
    echo "The specified debug keystore was not found: $signing_key" >&2
    exit 1
fi

cd "$repo_root"

dotnet run --project tests/WiiYiiHudNavigator.Yandex.Tests -c Release
dotnet run --project tests/YandexHudHeadUnit.Tests -c Release

build_args=(
    build src/YandexHudHeadUnit/YandexHudHeadUnit.csproj
    -c "$configuration" -t:SignAndroidPackage
    -p:PhoneBuild=true
    "-p:AndroidSdkDirectory=$android_sdk"
    "-p:JavaSdkDirectory=$java_sdk"
)
# With no key argument, .NET Android uses this developer's local debug key.
if [[ -n "$signing_key" ]]; then
    build_args+=(
        -p:AndroidKeyStore=true
        "-p:AndroidSigningKeyStore=$signing_key"
        -p:AndroidSigningKeyAlias=androiddebugkey
        -p:AndroidSigningStorePass=android
        -p:AndroidSigningKeyPass=android
    )
fi
dotnet "${build_args[@]}"

apk_name+=".apk"
mkdir -p "$output_directory"
cp "src/YandexHudHeadUnit/bin/$configuration/net9.0-android/com.avashield.yandexhud.phone-Signed.apk" \
    "$output_directory/$apk_name"
(
    cd "$output_directory"
    sha256sum "$apk_name" > SHA256SUMS.txt
)
echo "Built: $output_directory"

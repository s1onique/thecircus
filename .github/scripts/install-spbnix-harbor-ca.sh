#!/usr/bin/env bash
# Install the SPbNIX Harbor CA for the runner and generate BuildKit trust config.
# The certificate content is never printed.
set -euo pipefail

: "${SPBNIX_CA_CERT_PEM:?SPBNIX_CA_CERT_PEM must be supplied for Harbor publication}"
HARBOR_HOST="${HARBOR_HOST:-harbor-pve1.spbnix.local}"
RUNNER_TEMP="${RUNNER_TEMP:-/tmp}"
ca_dir="${RUNNER_TEMP}/circus-harbor-ca"
ca_file="${ca_dir}/${HARBOR_HOST}.pem"
buildkit_dir="${RUNNER_TEMP}/circus-buildkit"
config_file="${buildkit_dir}/buildkitd.toml"

mkdir -p "$ca_dir" "$buildkit_dir"
printf '%s\n' "$SPBNIX_CA_CERT_PEM" > "$ca_file"
chmod 0600 "$ca_file"
# Validate without emitting subject, issuer, dates, or PEM data.
openssl x509 -in "$ca_file" -noout >/dev/null

install_with_optional_sudo() {
    local source="$1"
    local destination="$2"
    local mode="$3"
    local parent
    parent="$(dirname "$destination")"
    if [[ -d "$parent" && -w "$parent" ]]; then
        install -m "$mode" "$source" "$destination"
    elif command -v sudo >/dev/null 2>&1 && sudo -n true >/dev/null 2>&1; then
        sudo install -D -m "$mode" "$source" "$destination"
    else
        return 1
    fi
}

# k9b's ARC DinD convention mounts daemon certs in the sidecar.  On a runner
# where that convention is not active, install the Docker certs.d entry when the
# runner can write it; the subsequent Harbor probe is the authoritative check.
if [[ "${SKIP_RUNNER_DOCKER_CERTS_D:-0}" != "1" ]]; then
    docker_ca_path="/etc/docker/certs.d/${HARBOR_HOST}/ca.crt"
    if install_with_optional_sudo "$ca_file" "$docker_ca_path" 0644; then
        echo "Docker certs.d CA installed for ${HARBOR_HOST}"
    else
        echo "Docker certs.d is not writable on this runner; expecting the configured daemon mount"
    fi
else
    echo "Skipping runner-side Docker certs.d; configured DinD sidecar owns daemon trust"
fi

# Install runner OS trust only when it is both meaningful and possible.  This
# is deliberately not a failure when the DinD sidecar is the established owner.
if [[ "${SKIP_RUNNER_SYSTEM_CA:-0}" != "1" ]]; then
    system_ca="/usr/local/share/ca-certificates/circus-harbor-ca.crt"
    if install_with_optional_sudo "$ca_file" "$system_ca" 0644; then
        if command -v update-ca-certificates >/dev/null 2>&1; then
            if [[ -w /etc/ssl/certs || ( $(command -v sudo >/dev/null 2>&1 && sudo -n true >/dev/null 2>&1; echo $?) -eq 0 ) ]]; then
                if command -v sudo >/dev/null 2>&1 && sudo -n true >/dev/null 2>&1; then
                    sudo update-ca-certificates >/dev/null
                else
                    update-ca-certificates >/dev/null
                fi
            fi
        fi
        echo "Runner system CA trust prepared"
    else
        echo "Runner system CA store is not writable; BuildKit config and daemon trust remain authoritative"
    fi
fi

cat > "$config_file" <<EOF
# Generated for this workflow run; the CA path is runner-local and ephemeral.
[registry."${HARBOR_HOST}"]
  ca = ["${ca_file}"]
EOF
chmod 0600 "$config_file"

if [[ -n "${GITHUB_OUTPUT:-}" ]]; then
    {
        printf 'ca_file=%s\n' "$ca_file"
        printf 'buildkitd_config=%s\n' "$config_file"
    } >> "$GITHUB_OUTPUT"
fi

echo "Harbor CA validated and BuildKit registry trust configured for ${HARBOR_HOST}"
echo "BuildKit config: ${config_file}"

#!/usr/bin/env bash
set -euo pipefail

cache_dir="${RUNNER_TEMP:?}/python-native-debs"
packages=(gdal-bin libatomic1)

if [[ "${PYTHON_NATIVE_CACHE_HIT:-false}" != true ]]; then
  download_dir="${RUNNER_TEMP}/python-native-download"
  mkdir -p "${download_dir}/partial" "${cache_dir}"
  apt_options=(-o Acquire::Retries=1 -o Acquire::http::Timeout=30 -o Acquire::https::Timeout=30)
  sudo timeout --kill-after=15s 120s apt-get "${apt_options[@]}" update --error-on=any -q
  sudo timeout --kill-after=15s 300s apt-get "${apt_options[@]}" install -yq --reinstall --download-only \
    -o "Dir::Cache::archives=${download_dir}" "${packages[@]}"
  cp "${download_dir}"/*.deb "${cache_dir}/"
  (cd "${cache_dir}" && sha256sum -- *.deb > SHA256SUMS)
fi

(cd "${cache_dir}" && sha256sum --check SHA256SUMS)
install_dir="${RUNNER_TEMP}/python-native-install"
mkdir -p "${install_dir}/partial"
cp "${cache_dir}"/*.deb "${install_dir}/"
sudo timeout --kill-after=15s 180s env DEBIAN_FRONTEND=noninteractive \
  apt-get -o "Dir::Cache::archives=${install_dir}" install -yq --no-download "${install_dir}"/*.deb
for package in "${packages[@]}"; do
  package_status=$(dpkg-query -W -f='${db:Status-Status}' "${package}")
  if [[ "${package_status}" != installed ]]; then
    echo "::error::Native dependency ${package} is not installed"
    exit 1
  fi
done
ogrinfo --version

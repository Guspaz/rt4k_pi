#!/usr/bin/env bash
# Builds a ready-to-flash Raspberry Pi OS Lite (64-bit) image with rt4k_pi preinstalled.
# Must run as root on a native ARM64 Linux host (the chroot executes the image's binaries).
# Usage: build-image.sh <path-to-rt4k_pi-executable> <output.img.xz>
set -euo pipefail

binary="$(realpath "$1")"
output="$(realpath -m "$2")"
source_url="${RASPIOS_URL:-https://downloads.raspberrypi.com/raspios_lite_arm64_latest}"
install_dir=/opt/rt4k_pi
hostname=rt4k

[ "$(id -u)" -eq 0 ] || { echo "Must run as root" >&2; exit 1; }
[ "$(uname -m)" = aarch64 ] || { echo "Must run on an ARM64 host" >&2; exit 1; }
[ -x "$binary" ] || chmod +x "$binary"

work="$(mktemp -d)"
root="$work/root"
loop=""

unmount() {
	for m in run sys proc dev/pts dev boot/firmware; do
		if mountpoint -q "$root/$m"; then umount "$root/$m"; fi
	done
	if mountpoint -q "$root"; then umount "$root"; fi
	if [ -n "$loop" ]; then losetup -d "$loop"; loop=""; fi
}

cleanup() {
	set +e
	unmount
	rm -rf "$work"
}
trap cleanup EXIT

echo "Downloading Raspberry Pi OS Lite..."
curl -fL --retry 3 -o "$work/source.img.xz" "$source_url"
xz -d -T0 "$work/source.img.xz"
image="$work/source.img"

# Leave room for the package upgrade
truncate -s +1536M "$image"
parted -s "$image" resizepart 2 100%

loop="$(losetup -Pf --show "$image")"
e2fsck -pf "${loop}p2" || [ $? -le 1 ]
resize2fs "${loop}p2"

mkdir -p "$root"
mount "${loop}p2" "$root"
mount "${loop}p1" "$root/boot/firmware"
mount --bind /dev "$root/dev"
mount --bind /dev/pts "$root/dev/pts"
mount -t proc proc "$root/proc"
mount -t sysfs sys "$root/sys"
mount -t tmpfs tmpfs "$root/run"

# Keep services from starting inside the chroot
printf '#!/bin/sh\nexit 101\n' > "$root/usr/sbin/policy-rc.d"
chmod +x "$root/usr/sbin/policy-rc.d"
cp /etc/resolv.conf "$root/etc/resolv.conf.build"
mv "$root/etc/resolv.conf" "$root/etc/resolv.conf.orig" 2>/dev/null || true
cp "$root/etc/resolv.conf.build" "$root/etc/resolv.conf"

echo "Updating packages..."
chroot "$root" /usr/bin/env DEBIAN_FRONTEND=noninteractive bash -euc '
	apt-get update
	apt-get -y -o Dpkg::Options::=--force-confdef -o Dpkg::Options::=--force-confold full-upgrade
	apt-get -y install fuse3 ksmbd-tools iw
	apt-get -y autoremove --purge
	apt-get clean
	rm -rf /var/lib/apt/lists/*
'

echo "Installing rt4k_pi..."
install -d "$root$install_dir"
install -m 0755 "$binary" "$root$install_dir/rt4k_pi"

cat > "$root/etc/systemd/system/rt4k.service" <<EOF
[Unit]
Description=rt4k_pi
After=network.target
StartLimitIntervalSec=0
[Service]
Type=simple
Restart=always
RestartSec=1
TimeoutStopSec=10
WorkingDirectory=$install_dir
ExecStart=$install_dir/rt4k_pi

[Install]
WantedBy=multi-user.target
EOF

# The Imager's customization may set a different hostname; force ours on every boot so the
# web UI is always reachable at http://rt4k.local
cat > "$root/usr/local/sbin/rt4k-hostname" <<EOF
#!/bin/sh
current="\$(cat /etc/hostname 2>/dev/null)"
if [ "\$current" != "$hostname" ]; then
	echo "$hostname" > /etc/hostname
	sed -i "s/^127\\.0\\.1\\.1.*/127.0.1.1\\t$hostname/" /etc/hosts
	grep -q '^127\\.0\\.1\\.1' /etc/hosts || printf '127.0.1.1\\t$hostname\\n' >> /etc/hosts
fi
hostname "$hostname"
EOF
chmod 0755 "$root/usr/local/sbin/rt4k-hostname"

cat > "$root/etc/systemd/system/rt4k-hostname.service" <<EOF
[Unit]
Description=Force rt4k hostname
DefaultDependencies=no
After=local-fs.target cloud-init-local.service
Before=network-pre.target avahi-daemon.service cloud-init.service
Wants=network-pre.target

[Service]
Type=oneshot
ExecStart=/usr/local/sbin/rt4k-hostname

[Install]
WantedBy=sysinit.target
EOF

chroot "$root" systemctl enable rt4k.service rt4k-hostname.service
echo "$hostname" > "$root/etc/hostname"
sed -i "s/^127\.0\.1\.1.*/127.0.1.1\t$hostname/" "$root/etc/hosts"

# Undo build-only changes and anything that must be unique per device
rm -f "$root/usr/sbin/policy-rc.d" "$root/etc/resolv.conf.build"
if [ -e "$root/etc/resolv.conf.orig" ] || [ -L "$root/etc/resolv.conf.orig" ]; then
	mv -f "$root/etc/resolv.conf.orig" "$root/etc/resolv.conf"
fi
rm -f "$root"/etc/ssh/ssh_host_*
: > "$root/etc/machine-id"
rm -rf "$root"/tmp/* "$root"/var/tmp/*
find "$root/var/log" -type f -exec truncate -s 0 {} +

sync
unmount

echo "Compressing image..."
mkdir -p "$(dirname "$output")"
extract_size="$(stat -c %s "$image")"
extract_sha256="$(sha256sum "$image" | cut -d' ' -f1)"
xz -T0 -6 -c "$image" > "$output"
(cd "$(dirname "$output")" && sha256sum "$(basename "$output")" > "$(basename "$output").sha256")

# Raspberry Pi Imager repository file. Imager only offers its customization screen (Wi-Fi,
# user, SSH) for images listed in a repository with an init_format, not for "Use custom".
if [ -n "${IMAGE_URL:-}" ]; then
    cat > "$(dirname "$output")/rt4k_pi.json" <<EOF
{
  "os_list": [
    {
      "name": "rt4k_pi${IMAGE_VERSION:+ $IMAGE_VERSION}",
      "description": "Raspberry Pi OS Lite with rt4k_pi preinstalled (Raspberry Pi Zero 2 W)",
      "icon": "https://downloads.raspberrypi.com/raspios_armhf/Raspberry_Pi_OS_(32-bit).png",
      "url": "$IMAGE_URL",
      "extract_size": $extract_size,
      "extract_sha256": "$extract_sha256",
      "image_download_size": $(stat -c %s "$output"),
      "release_date": "$(date -u +%Y-%m-%d)",
      "init_format": "cloudinit-rpi",
      "devices": ["pi3-64bit"]
    }
  ]
}
EOF
fi
echo "Wrote $output"

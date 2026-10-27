#!/usr/bin/env sh
set -eu

# Refresh before clamd starts so the daemon cannot retain the image's packaged
# database when FreshClam finishes before the notification socket exists. A
# feed outage is reported but does not erase a retained database; Evidence
# independently enforces the unchanged 72-hour freshness policy.
if ! freshclam --stdout --user=clamav; then
    echo >&2 'FreshClam pre-start refresh failed; clamd will load retained signatures and Evidence will enforce the 72-hour freshness gate.'
fi

exec /init-unprivileged

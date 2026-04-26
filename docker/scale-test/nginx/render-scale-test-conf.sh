#!/bin/sh
set -eu

template_path="/etc/nginx/scale-test.conf.template"
output_path="/etc/nginx/nginx.conf"
canary_enabled="${HONUA_SCALE_TEST_CANARY_ENABLED:-false}"
canary_weight="${HONUA_SCALE_TEST_CANARY_WEIGHT:-0}"

case "$canary_weight" in
    ''|*[!0-9]*)
        echo "HONUA_SCALE_TEST_CANARY_WEIGHT must be an integer between 0 and 100" >&2
        exit 1
        ;;
esac

if [ "$canary_weight" -lt 0 ] || [ "$canary_weight" -gt 100 ]; then
    echo "HONUA_SCALE_TEST_CANARY_WEIGHT must be an integer between 0 and 100" >&2
    exit 1
fi

CANARY_HTTP_BLOCK=""
CANARY_SERVER_BLOCK=""
CANARY_HEALTH_DECISION_BLOCK=""
CANARY_TRAFFIC_DECISION_BLOCK=""
CANARY_NAMED_LOCATION_BLOCK=""

if [ "$canary_enabled" = "true" ]; then
    CANARY_HTTP_BLOCK=$(cat <<EOF
    upstream honua_canary {
        zone honua_canary 64k;
        least_conn;
        server honua_canary:8080 max_fails=1 fail_timeout=5s;
        keepalive 8;
    }

    map \$http_x_honua_canary \$honua_force_canary {
        default 0;
        "always" 1;
    }

    split_clients "\${remote_addr}\${http_user_agent}\${request_uri}\${msec}" \$honua_weighted_lane {
        ${canary_weight}% canary;
        * stable;
    }

    map "\$honua_force_canary:\$honua_weighted_lane" \$honua_route_lane {
        "~^1:" canary;
        "~^0:canary\$" canary;
        default stable;
    }
EOF
)

    CANARY_SERVER_BLOCK="        error_page 418 = @honua_canary;"
    CANARY_HEALTH_DECISION_BLOCK='            if ($http_x_honua_canary = "always") { return 418; }'
    CANARY_TRAFFIC_DECISION_BLOCK='            if ($honua_route_lane = canary) { return 418; }'
    CANARY_NAMED_LOCATION_BLOCK=$(cat <<EOF
        location @honua_canary {
            proxy_pass http://honua_canary;
            proxy_set_header Host \$host;
            proxy_set_header X-Real-IP \$remote_addr;
            proxy_set_header X-Forwarded-For \$proxy_add_x_forwarded_for;
            proxy_set_header X-Forwarded-Proto \$scheme;
            proxy_set_header X-Instance-ID \$upstream_addr;
            add_header X-Instance-ID \$upstream_addr always;
            add_header X-Honua-Deployment-Lane canary always;
            add_header X-Honua-Canary-Weight ${canary_weight} always;
            proxy_connect_timeout 10s;
            proxy_read_timeout 300s;
            proxy_send_timeout 60s;
            proxy_http_version 1.1;
            proxy_set_header Connection "";
        }
EOF
)
fi

export CANARY_HTTP_BLOCK
export CANARY_SERVER_BLOCK
export CANARY_HEALTH_DECISION_BLOCK
export CANARY_TRAFFIC_DECISION_BLOCK
export CANARY_NAMED_LOCATION_BLOCK

envsubst \
    '${CANARY_HTTP_BLOCK} ${CANARY_SERVER_BLOCK} ${CANARY_HEALTH_DECISION_BLOCK} ${CANARY_TRAFFIC_DECISION_BLOCK} ${CANARY_NAMED_LOCATION_BLOCK}' \
    < "$template_path" > "$output_path"

nginx -t -c "$output_path" >/dev/null

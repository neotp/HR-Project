#!/bin/sh
set -eu

template="/usr/share/nginx/html/appsettings.template.json"
output="/usr/share/nginx/html/appsettings.json"

envsubst '${AZURE_AD_AUTHORITY} ${AZURE_AD_CLIENT_ID} ${API_BASE_URL} ${API_SCOPE}' \
  < "$template" > "$output"


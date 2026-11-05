#!/usr/bin/env bash
# Enable live AI on the deployed Haru API.
# The token is stored as the Container Apps secret "copilot-github-token". No file stores it.
# The token goes to az through stdin, not through the command line, so `ps` does not show it.
#
# Create the token first: GitHub > Settings > Developer settings > Fine-grained personal access tokens.
# Give it the account permission "Copilot Requests" and nothing else.
# A classic "ghp_" token does not work with the Copilot SDK.
#
# Usage:
#   scripts/set-copilot-token.sh           prompt for the token (input hidden)
#   scripts/set-copilot-token.sh --clear   remove the token (the app replays the recorded run again)
# Optional: HARU_RG (default rg-haru), HARU_APP (default api)
set -euo pipefail

RG="${HARU_RG:-rg-haru}"
APP="${HARU_APP:-api}"
SECRET="copilot-github-token"

command -v az >/dev/null || { echo "Azure CLI (az) is required." >&2; exit 1; }

if [[ "${1:-}" == "--clear" ]]; then
  TOKEN="unset"
  EXPECT="false"
else
  read -rsp "Fine-grained token with only 'Copilot Requests' (input hidden): " TOKEN
  echo
  [[ -n "$TOKEN" ]] || { echo "Empty token." >&2; exit 1; }
  case "$TOKEN" in
    github_pat_*) ;;
    ghp_*) echo "Classic tokens (ghp_) are rejected by the Copilot SDK. Create a fine-grained token." >&2; exit 1 ;;
    gho_*) echo "Do not copy a personal login token (gho_) to Azure. Create a fine-grained token." >&2; exit 1 ;;
    *) echo "Warning: a fine-grained token starts with 'github_pat_'." >&2 ;;
  esac
  # Hidden input makes a double paste easy. The Copilot runtime then rejects the token.
  if [[ "$TOKEN" == *github_pat_*github_pat_* ]]; then
    echo "The token was pasted more than once. Paste it one time only." >&2; exit 1
  fi
  if [[ "$TOKEN" =~ [[:space:]] ]]; then
    echo "The token contains a space. Copy it again." >&2; exit 1
  fi
  EXPECT="true"
fi

# printf is a shell builtin. az reads "@/dev/stdin" as the argument value.
printf '%s=%s' "$SECRET" "$TOKEN" | az containerapp secret set -g "$RG" -n "$APP" --secrets @/dev/stdin -o none
unset TOKEN

# Secrets are read when a replica starts, so restart the active revision.
REV="$(az containerapp revision list -g "$RG" -n "$APP" --query "[?properties.active] | [0].name" -o tsv)"
az containerapp revision restart -g "$RG" -n "$APP" --revision "$REV" -o none

FQDN="$(az containerapp show -g "$RG" -n "$APP" --query properties.configuration.ingress.fqdn -o tsv)"
echo "Waiting for https://$FQDN to report aiAvailable=$EXPECT ..."
for _ in $(seq 1 30); do
  sleep 5
  if curl -fsS "https://$FQDN/api/config" 2>/dev/null | grep -q "\"aiAvailable\":$EXPECT"; then
    echo "Done: aiAvailable=$EXPECT at https://$FQDN"
    exit 0
  fi
done

echo "aiAvailable did not become $EXPECT in 150 s." >&2
echo "Check the token permission, then run: az containerapp logs show -g $RG -n $APP --tail 50" >&2
exit 1

#!/usr/bin/env bash
# Run COHAD with in-memory data and dev JWT auth (no Cosmos, no Azure AD B2C).
# The API listens on HTTPS only (port 5001) so features that require a secure context work locally.
#
# Usage:
#   ./scripts/run-mock-data.sh           Print instructions (default)
#   ./scripts/run-mock-data.sh api       Generate signing keys and start the API (same as the one-liner below)

set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

MOCK_URLS='https://127.0.0.1:5001'

if [[ "${1:-}" == "api" || "${1:-}" == "--api" ]]; then
  exec env \
    MockJwt__SigningKey="$(openssl rand -hex 32)" \
    UnsubscribeToken__SigningKey="$(openssl rand -hex 32)" \
    ASPNETCORE_ENVIRONMENT=MockData \
    ASPNETCORE_URLS="$MOCK_URLS" \
    dotnet run --project "$ROOT/Web/Web.csproj"
fi

if [[ -n "${1:-}" ]]; then
  echo "Usage: $0 [api]" >&2
  echo "  (no args)  Show how to run Angular + API in MockData mode" >&2
  echo "  api        Generate MockJwt/UnsubscribeToken secrets and run the API on $MOCK_URLS" >&2
  exit 1
fi

ONELINER='MockJwt__SigningKey="$(openssl rand -hex 32)" UnsubscribeToken__SigningKey="$(openssl rand -hex 32)" ASPNETCORE_ENVIRONMENT=MockData ASPNETCORE_URLS="'"$MOCK_URLS"'" dotnet run --project "'"$ROOT"'/Web/Web.csproj"'

echo "From repo root ($ROOT):"
echo ""
echo "  One-time — trust the ASP.NET Core dev HTTPS certificate (stops browser warnings):"
echo "    dotnet dev-certs https --trust"
echo ""
echo "  Terminal 1 — Angular (mock auth):"
echo "    cd Web/ClientApp && npm run start:mock"
echo ""
echo "  Terminal 2 — API (copy-paste; fresh random keys each run):"
echo "    $ONELINER"
echo ""
echo "  Or from repo root, same effect as Terminal 2:"
echo "    ./scripts/run-mock-data.sh api"
echo ""
echo "Open $MOCK_URLS — signed in as mock@cohad.local (admin), with two seeded homes and taylor@cohad.local to manage."
echo ""
echo "Optional — email job mock simulation (see Web/appsettings.MockData.json EmailJobs:Mock):"
echo "  EmailJobs__Mock__DelayMilliseconds=500"
echo "  EmailJobs__Mock__RandomFailureProbability=0.3"
echo "  EmailJobs__Mock__FailAllRecipients=true"
echo "  EmailJobs__Mock__RandomFailSeed=42"
echo "  EmailJobs__Mock__JobFatalError='Simulated fatal job error'"

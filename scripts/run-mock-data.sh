#!/usr/bin/env bash
# Run COHAD with in-memory data and dev JWT auth (no Cosmos, no Azure AD B2C).
# Terminal 1: Angular with mock auth
# Terminal 2: API with MockData environment (proxies to Angular like Development)

set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
echo "From repo root ($ROOT):"
echo "  Terminal 1: cd Web/ClientApp && npm run start:mock"
echo "  Terminal 2: set MockJwt__SigningKey to a local-only secret (32+ characters), then:"
echo "    MockJwt__SigningKey='<secret>' ASPNETCORE_ENVIRONMENT=MockData ASPNETCORE_URLS=\"http://127.0.0.1:5000\" dotnet run --project \"$ROOT/Web/Web.csproj\""
echo ""
echo "Open http://127.0.0.1:5000 — you should be signed in as mock@cohad.local with a sample home to edit."

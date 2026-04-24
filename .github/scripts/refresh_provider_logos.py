"""Refresh LogoPath values in HurrahTv.Shared/Models/StreamingService.cs against TMDb.

Runs in CI on a cron. Fetches the current /watch/providers/tv response from TMDb and
rewrites any stale logo_path values in place. Reads TMDB_API_KEY from the environment.

Exits 0 on success (with or without changes). Exits non-zero only on fetch/parse failure,
so peter-evans/create-pull-request can detect a clean no-op vs. a real problem.
"""

import json
import os
import re
import sys
import urllib.request

SERVICE_FILE = "HurrahTv.Shared/Models/StreamingService.cs"

# providers we ship — mirror the list in StreamingService.All
KNOWN_PROVIDER_IDS = [8, 9, 15, 337, 2303, 386, 1899, 350]


def fetch_tmdb_providers(api_key: str) -> dict[int, str]:
    url = (
        "https://api.themoviedb.org/3/watch/providers/tv"
        f"?api_key={api_key}&watch_region=US"
    )
    with urllib.request.urlopen(url, timeout=30) as response:
        data = json.load(response)
    return {p["provider_id"]: p["logo_path"] for p in data["results"]}


def update_logo_paths(content: str, logos: dict[int, str]) -> tuple[str, list[str]]:
    changes: list[str] = []
    for pid in KNOWN_PROVIDER_IDS:
        if pid not in logos:
            print(f"WARN: provider {pid} not in TMDb response", file=sys.stderr)
            continue
        new_logo = logos[pid]
        # match: TmdbProviderId = 8, ... LogoPath = "/path.jpg"
        pattern = re.compile(
            rf'(TmdbProviderId\s*=\s*{pid}\s*,[^}}]*?LogoPath\s*=\s*")([^"]*)(")',
            re.DOTALL,
        )
        match = pattern.search(content)
        if not match:
            print(f"WARN: no LogoPath match for provider {pid}", file=sys.stderr)
            continue
        old_logo = match.group(2)
        if old_logo == new_logo:
            continue
        content = content[: match.start(2)] + new_logo + content[match.end(2) :]
        changes.append(f"{pid}: {old_logo} -> {new_logo}")
    return content, changes


def main() -> int:
    api_key = os.environ.get("TMDB_API_KEY")
    if not api_key:
        print("ERROR: TMDB_API_KEY not set", file=sys.stderr)
        return 2

    try:
        logos = fetch_tmdb_providers(api_key)
    except Exception as exc:
        print(f"ERROR: TMDb fetch failed: {exc}", file=sys.stderr)
        return 3

    with open(SERVICE_FILE) as f:
        original = f.read()

    updated, changes = update_logo_paths(original, logos)

    if not changes:
        print("no changes — all logo paths current")
        return 0

    with open(SERVICE_FILE, "w") as f:
        f.write(updated)

    print("updated logo paths:")
    for change in changes:
        print(f"  {change}")
    return 0


if __name__ == "__main__":
    sys.exit(main())

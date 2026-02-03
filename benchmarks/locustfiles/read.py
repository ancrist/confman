"""Read benchmark — GET config entries from cluster."""
from __future__ import annotations

import json
import os
import random

from locust import HttpUser, task, constant

_key_cache: list[dict[str, str]] | None = None


def load_keys() -> list[dict[str, str]]:
    """Load key registry from file. Cached at module level (once per process)."""
    global _key_cache
    if _key_cache is None:
        path = os.environ["BENCH_KEY_REGISTRY_FILE"]
        with open(path) as f:
            _key_cache = json.load(f)
    return _key_cache


class ConfigReader(HttpUser):
    wait_time = constant(0)

    def on_start(self) -> None:
        self.api_key: str = os.environ["BENCH_API_KEY"]
        self.keys: list[dict[str, str]] = load_keys()

    @task
    def read_config(self) -> None:
        entry = random.choice(self.keys)
        with self.client.get(
            f"/api/v1/namespaces/{entry['namespace']}/config/{entry['key']}",
            headers={"X-Api-Key": self.api_key},
            name="/config/[key] GET",
            catch_response=True,
        ) as response:
            if response.status_code >= 400:
                response.failure(f"HTTP {response.status_code}")

"""Mixed 80/20 read/write benchmark."""
from __future__ import annotations

import base64
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


class MixedUser(HttpUser):
    wait_time = constant(0)

    def on_start(self) -> None:
        self.api_key: str = os.environ["BENCH_API_KEY"]
        self.keys: list[dict[str, str]] = load_keys()
        self.namespaces: list[str] = json.loads(os.environ["BENCH_NAMESPACES"])
        self.payload_size: int = int(os.environ.get("BENCH_PAYLOAD_SIZE", "1024"))
        self._payload: str = base64.b64encode(
            os.urandom(self.payload_size)
        ).decode("ascii")[:self.payload_size]

    @task(8)
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

    @task(2)
    def write_config(self) -> None:
        ns = random.choice(self.namespaces)
        key = f"key-{os.urandom(6).hex()}"
        with self.client.put(
            f"/api/v1/namespaces/{ns}/config/{key}",
            json={"value": self._payload},
            headers={"X-Api-Key": self.api_key},
            name="/config/[key] PUT",
            catch_response=True,
        ) as response:
            if response.status_code >= 400:
                response.failure(f"HTTP {response.status_code}")

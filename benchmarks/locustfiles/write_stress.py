"""Stress-test write benchmark — PUT config entries with no think time.

Measures peak write throughput by firing requests as fast as possible,
which exercises the write-batching optimization (multiple requests
arriving within the 1ms batch window get grouped into a single Raft entry).
"""
from __future__ import annotations

import base64
import json
import os
from itertools import count

from locust import HttpUser, task, constant

_counter = count()


class ConfigWriterStress(HttpUser):
    wait_time = constant(0)  # No think time — fire as fast as possible

    def on_start(self) -> None:
        self.api_key: str = os.environ["BENCH_API_KEY"]
        self.namespaces: list[str] = json.loads(os.environ["BENCH_NAMESPACES"])
        self.payload_size: int = int(os.environ.get("BENCH_PAYLOAD_SIZE", "1024"))
        self._payload: str = base64.b64encode(
            os.urandom(self.payload_size)
        ).decode("ascii")[:self.payload_size]
        self._user_id: int = next(_counter)

    @task
    def write_config(self) -> None:
        ns = self.namespaces[self._user_id % len(self.namespaces)]
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

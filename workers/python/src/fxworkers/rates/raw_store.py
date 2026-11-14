"""Immutable raw store: every fetched response is kept byte for byte, so any load can be replayed offline.

Locally this is Azurite (docker-compose); in Azure it is a Blob container. A plain directory works for tests.
Objects are never overwritten: the key carries the fetch time and the content hash.
"""

from __future__ import annotations

import datetime as dt
import hashlib
import os
from dataclasses import dataclass
from pathlib import Path
from typing import Protocol

# Azurite's well-known development connection string (not a secret).
AZURITE = (
    "DefaultEndpointsProtocol=http;AccountName=devstoreaccount1;"
    "AccountKey=Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==;"
    "BlobEndpoint=http://127.0.0.1:10000/devstoreaccount1;"
)
DEFAULT_CONTAINER = "raw-rates"


@dataclass(frozen=True)
class RawObject:
    key: str
    sha256: str
    data: bytes


def sha256(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def make_key(provider: str, series_id: str, fetched_at: dt.datetime, digest: str) -> str:
    ts = fetched_at.astimezone(dt.timezone.utc)
    return f"{provider}/{series_id}/{ts:%Y/%m/%d}/{ts:%Y%m%dT%H%M%SZ}-{digest[:12]}.json"


class RawStore(Protocol):
    def put(self, key: str, data: bytes, metadata: dict[str, str]) -> None: ...
    def get(self, key: str) -> bytes: ...


class LocalRawStore:
    def __init__(self, root: Path) -> None:
        self.root = root

    def put(self, key: str, data: bytes, metadata: dict[str, str]) -> None:
        path = self.root / key
        path.parent.mkdir(parents=True, exist_ok=True)
        with open(path, "xb") as f:  # "x": refuse to overwrite
            f.write(data)

    def get(self, key: str) -> bytes:
        return (self.root / key).read_bytes()


class BlobRawStore:
    def __init__(self, connection_string: str, container: str = DEFAULT_CONTAINER) -> None:
        from azure.core.exceptions import ResourceExistsError
        from azure.storage.blob import BlobServiceClient

        self._container = BlobServiceClient.from_connection_string(connection_string).get_container_client(container)
        try:
            self._container.create_container()
        except ResourceExistsError:
            pass

    def put(self, key: str, data: bytes, metadata: dict[str, str]) -> None:
        self._container.upload_blob(key, data, overwrite=False, metadata=metadata)

    def get(self, key: str) -> bytes:
        return self._container.download_blob(key).readall()


def open_store() -> RawStore:
    """FX_RAW_STORE_DIR → a local directory; otherwise Blob (FX_RAW_STORE_CONNECTION, default Azurite)."""
    directory = os.environ.get("FX_RAW_STORE_DIR")
    if directory:
        return LocalRawStore(Path(directory))
    return BlobRawStore(
        os.environ.get("FX_RAW_STORE_CONNECTION", AZURITE),
        os.environ.get("FX_RAW_STORE_CONTAINER", DEFAULT_CONTAINER),
    )


def put_raw(store: RawStore, provider: str, series_id: str, data: bytes, metadata: dict[str, str],
            fetched_at: dt.datetime | None = None) -> RawObject:
    fetched_at = fetched_at or dt.datetime.now(dt.timezone.utc)
    digest = sha256(data)
    key = make_key(provider, series_id, fetched_at, digest)
    store.put(key, data, {**metadata, "sha256": digest, "fetched_at": fetched_at.isoformat()})
    return RawObject(key, digest, data)


def get_raw(store: RawStore, key: str) -> RawObject:
    data = store.get(key)
    return RawObject(key, sha256(data), data)

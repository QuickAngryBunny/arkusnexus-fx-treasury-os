"""Immutable raw store: every fetched response is kept byte for byte, so any load can be replayed offline.

Hosted (Firebase / Google Cloud) this is a Cloud Storage bucket (FX_RAW_STORE_URL=gs://bucket/prefix). Locally it is
Azurite (docker-compose), and a plain directory works for tests.
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
    def list(self, prefix: str) -> list[str]: ...


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

    def list(self, prefix: str) -> list[str]:
        """Keys under a prefix, sorted (keys sort by fetch time within a series)."""
        base = self.root / prefix
        if not base.exists():
            return []
        return sorted(str(p.relative_to(self.root)).replace("\\", "/") for p in base.rglob("*") if p.is_file())


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

    def list(self, prefix: str) -> list[str]:
        return sorted(b.name for b in self._container.list_blobs(name_starts_with=prefix))


class GcsRawStore:
    """A Cloud Storage bucket, with Application Default Credentials (the Cloud Run service account)."""

    def __init__(self, bucket: str, prefix: str = "", client=None) -> None:
        if client is None:
            from google.cloud import storage  # optional dependency: pip install "fxworkers[gcp]"

            client = storage.Client()
        self._bucket = client.bucket(bucket)
        self._prefix = prefix.strip("/") + "/" if prefix.strip("/") else ""

    def put(self, key: str, data: bytes, metadata: dict[str, str]) -> None:
        blob = self._bucket.blob(self._prefix + key)
        blob.metadata = metadata
        # if_generation_match=0: create only, never overwrite (the precondition fails if the object exists).
        blob.upload_from_string(data, content_type="application/json", if_generation_match=0)

    def get(self, key: str) -> bytes:
        return self._bucket.blob(self._prefix + key).download_as_bytes()

    def list(self, prefix: str) -> list[str]:
        start = len(self._prefix)
        return sorted(b.name[start:] for b in self._bucket.list_blobs(prefix=self._prefix + prefix))


def open_store() -> RawStore:
    """FX_RAW_STORE_DIR → a local directory; FX_RAW_STORE_URL=gs://bucket[/prefix] → Cloud Storage;
    otherwise Blob (FX_RAW_STORE_CONNECTION, default Azurite)."""
    directory = os.environ.get("FX_RAW_STORE_DIR")
    if directory:
        return LocalRawStore(Path(directory))
    url = os.environ.get("FX_RAW_STORE_URL", "")
    if url:
        if not url.startswith("gs://") or len(url) <= 5:
            raise ValueError("FX_RAW_STORE_URL must look like gs://bucket or gs://bucket/prefix")
        bucket, _, prefix = url[5:].partition("/")
        return GcsRawStore(bucket, prefix)
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

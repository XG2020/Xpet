"""Explicit editing APIs; the agent has no workspace-memory mutation tool."""
from __future__ import annotations

import asyncio
from collections.abc import Callable
from typing import Literal

from fastapi import APIRouter
from pydantic import BaseModel, ConfigDict, Field

from errors import ServiceError
from workspace_memory import MAX_DOCUMENT_CHARS, MAX_SUMMARY_CHARS, WorkspaceMemoryStore


class SummaryWrite(BaseModel):
    model_config = ConfigDict(extra="forbid")
    content: str = Field(max_length=MAX_SUMMARY_CHARS)
    revision: str = Field(max_length=64, pattern=r"^(?:[a-f0-9]{64})?$")


class DocumentWrite(BaseModel):
    model_config = ConfigDict(extra="forbid")
    title: str = Field(min_length=1, max_length=100)
    content: str = Field(min_length=1, max_length=MAX_DOCUMENT_CHARS)
    perspective: Literal["user", "self"] = "user"
    revision: str = Field(default="", max_length=64, pattern=r"^(?:[a-f0-9]{64})?$")


class DocumentDelete(BaseModel):
    model_config = ConfigDict(extra="forbid")
    revision: str = Field(min_length=64, max_length=64, pattern=r"^[a-f0-9]{64}$")


def build_workspace_memory_router(current_character: Callable[[str | None], str], store_for: Callable[[str], WorkspaceMemoryStore]) -> APIRouter:
    router = APIRouter(prefix="/api/workspace-memory", tags=["workspace-memory"])

    @router.get("/summary")
    async def read_summary(character_id: str | None = None):
        identity = current_character(character_id)
        summary = await asyncio.to_thread(store_for(identity).read_summary)
        return {"character_id": identity, "summary": summary}

    @router.put("/summary")
    async def save_summary(payload: SummaryWrite, character_id: str | None = None):
        identity = current_character(character_id)
        summary = await asyncio.to_thread(store_for(identity).save, payload.content, revision=payload.revision)
        return {"character_id": identity, "summary": summary}

    @router.get("/documents")
    async def list_documents(character_id: str | None = None):
        identity = current_character(character_id)
        documents = await asyncio.to_thread(store_for(identity).list_documents)
        return {"character_id": identity, "documents": documents}

    @router.post("/documents")
    async def create_document(payload: DocumentWrite, character_id: str | None = None):
        identity = current_character(character_id)
        if payload.revision:
            raise ServiceError("新文档不能附带已有版本。", 422)
        document = await asyncio.to_thread(store_for(identity).create_document, payload.title, payload.content, payload.perspective)
        return {"character_id": identity, "document": document}

    @router.get("/documents/{document_id}")
    async def read_document(document_id: str, character_id: str | None = None):
        identity = current_character(character_id)
        document = await asyncio.to_thread(store_for(identity).read, document_id)
        if not document or document_id == "summary":
            raise ServiceError("找不到当前角色的这份记忆文档。", 404)
        return {"character_id": identity, "document": document}

    @router.put("/documents/{document_id}")
    async def save_document(document_id: str, payload: DocumentWrite, character_id: str | None = None):
        identity = current_character(character_id)
        store = store_for(identity)
        existing = await asyncio.to_thread(store.read, document_id)
        if not existing or document_id == "summary":
            raise ServiceError("找不到当前角色的这份记忆文档。", 404)
        document = await asyncio.to_thread(store.save, payload.content, title=payload.title, perspective=payload.perspective,
                                         record_id=document_id, revision=payload.revision)
        return {"character_id": identity, "document": document}

    @router.delete("/documents/{document_id}")
    async def delete_document(document_id: str, payload: DocumentDelete, character_id: str | None = None):
        identity = current_character(character_id)
        if not await asyncio.to_thread(store_for(identity).delete_document, document_id, payload.revision):
            raise ServiceError("找不到当前角色的这份记忆文档。", 404)
        return {"character_id": identity, "ok": True, "id": document_id}

    return router

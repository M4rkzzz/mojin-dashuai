"""Existing MB/VW forwarding behavior with bounded, payload-free connection diagnostics."""
from __future__ import annotations
import asyncio
import contextlib
import json
import os
import time

LISTEN_PORT = int(os.getenv("LISTEN_PORT", "25503"))
TARGET_HOST = os.getenv("TARGET_HOST", "gsmanager")
TARGET_PORT = int(os.getenv("TARGET_PORT", "25503"))

def diagnostic(stage, started, **fields):
    print(json.dumps(dict(stage=stage, port=LISTEN_PORT,
        elapsedMs=round((time.monotonic()-started)*1000), **fields)), flush=True)

async def copy_stream(reader, writer):
    count, reason = 0, "eof"
    try:
        while data := await reader.read(65_536):
            writer.write(data)
            await writer.drain()
            count += len(data)
    except asyncio.CancelledError:
        reason = "peer-closed"
    except (ConnectionError, OSError) as error:
        reason = type(error).__name__
    finally:
        writer.close()
        with contextlib.suppress(ConnectionError, OSError):
            await writer.wait_closed()
    return count, reason

async def handle_client(client_reader, client_writer):
    started = time.monotonic()
    try:
        server_reader, server_writer = await asyncio.open_connection(TARGET_HOST, TARGET_PORT)
    except (ConnectionError, OSError) as error:
        diagnostic("upstream-connect", started, success=False, reason=type(error).__name__)
        client_writer.close()
        with contextlib.suppress(ConnectionError, OSError):
            await client_writer.wait_closed()
        return
    diagnostic("upstream-connect", started, success=True)
    tasks = [asyncio.create_task(copy_stream(client_reader, server_writer)),
             asyncio.create_task(copy_stream(server_reader, client_writer))]
    try:
        done, pending = await asyncio.wait(tasks, return_when=asyncio.FIRST_COMPLETED)
        for task in pending:
            task.cancel()
        results = await asyncio.gather(*tasks, return_exceptions=True)
        safe = [row if isinstance(row, tuple) else (0, type(row).__name__) for row in results]
        diagnostic("forward-closed", started, upBytes=safe[0][0], downBytes=safe[1][0],
                   upEnd=safe[0][1], downEnd=safe[1][1])
    finally:
        for task in tasks:
            if not task.done():
                task.cancel()
        await asyncio.gather(*tasks, return_exceptions=True)

async def main():
    server = await asyncio.start_server(handle_client, "0.0.0.0", LISTEN_PORT)
    print(json.dumps(dict(stage="listening",port=LISTEN_PORT,targetPort=TARGET_PORT)), flush=True)
    async with server:
        await server.serve_forever()

if __name__ == "__main__":
    asyncio.run(main())

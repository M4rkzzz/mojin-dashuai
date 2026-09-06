import asyncio
import contextlib
import importlib.util
import io
import json
from pathlib import Path
import unittest
from unittest.mock import patch

spec=importlib.util.spec_from_file_location('proxy',Path(__file__).resolve().parents[1]/'deploy/network/tcp_proxy.py')
proxy=importlib.util.module_from_spec(spec);spec.loader.exec_module(proxy)

class ProxyTests(unittest.IsolatedAsyncioTestCase):
    async def test_stream_round_trip_and_payload_free_diagnostics(self):
        finished=asyncio.Event()
        async def echo(reader,writer):
            try:
                while data:=await reader.read(65536):
                    writer.write(data);await writer.drain()
            finally:
                writer.close();await writer.wait_closed()
        async def forward(reader,writer):
            try:await proxy.handle_client(reader,writer)
            finally:finished.set()
        async with await asyncio.start_server(echo,'127.0.0.1',0) as backend:
            with patch.object(proxy,'TARGET_HOST','127.0.0.1'),patch.object(proxy,'TARGET_PORT',backend.sockets[0].getsockname()[1]),contextlib.redirect_stdout(io.StringIO()) as output:
                async with await asyncio.start_server(forward,'127.0.0.1',0) as frontend:
                    reader,writer=await asyncio.open_connection('127.0.0.1',frontend.sockets[0].getsockname()[1])
                    data=b'private-handshake-ticket\x00'+bytes(range(256))*4096
                    writer.write(data);await writer.drain()
                    self.assertEqual(data,await asyncio.wait_for(reader.readexactly(len(data)),5))
                    writer.close();await writer.wait_closed();await asyncio.wait_for(finished.wait(),5)
            rows=[json.loads(s) for s in output.getvalue().splitlines()]
            self.assertEqual(len(data),rows[-1]['upBytes']);self.assertEqual(len(data),rows[-1]['downBytes'])
            self.assertNotIn('private-handshake',output.getvalue())

    async def test_refused_upstream_closes_client_and_reports_reason(self):
        finished=asyncio.Event()
        async def forward(reader,writer):
            try:await proxy.handle_client(reader,writer)
            finally:finished.set()
        with patch.object(proxy.asyncio,'open_connection',side_effect=ConnectionRefusedError()),contextlib.redirect_stdout(io.StringIO()) as output:
            reader=asyncio.StreamReader()
            from unittest.mock import AsyncMock,Mock
            writer=Mock();writer.wait_closed=AsyncMock()
            await forward(reader,writer)
            writer.close.assert_called_once()
        self.assertEqual('ConnectionRefusedError',json.loads(output.getvalue())['reason'])

if __name__=='__main__':unittest.main()

#!/usr/bin/env node
/**
 * Export the MCP tool schemas to a folder (e.g. an MCP client's tool cache).
 * Run after adding server.tool() entries in src/index.mjs:
 *
 *   UNITY_BRIDGE_MCP_TOOLS_DIR=/path/to/out node mcp/scripts/export-mcp-tool-descriptors.mjs
 *
 * Set UNITY_BRIDGE_MCP_TOOLS_DIR to your client's tool-cache directory; if unset it
 * defaults to ./mcp-tools next to this repo.
 */
import { writeFileSync, mkdirSync } from 'fs';
import { join, dirname } from 'path';
import { fileURLToPath } from 'url';
import { Client } from '@modelcontextprotocol/sdk/client/index.js';
import { StdioClientTransport } from '@modelcontextprotocol/sdk/client/stdio.js';

const __dirname = dirname(fileURLToPath(import.meta.url));
const bridgeRoot = join(__dirname, '..');
const projectRoot = join(bridgeRoot, '../..');
const outDir =
  process.env.UNITY_BRIDGE_MCP_TOOLS_DIR || join(projectRoot, 'mcp-tools');

const transport = new StdioClientTransport({
  command: 'node',
  args: ['src/index.mjs'],
  cwd: bridgeRoot,
});

const client = new Client({ name: 'descriptor-export', version: '1.0.0' });
await client.connect(transport);
const { tools } = await client.listTools();
await client.close();

mkdirSync(outDir, { recursive: true });
for (const tool of tools) {
  const path = join(outDir, `${tool.name}.json`);
  writeFileSync(
    path,
    JSON.stringify(
      {
        name: tool.name,
        description: tool.description ?? '',
        arguments: tool.inputSchema ?? { type: 'object', properties: {} },
      },
      null,
      2,
    ) + '\n',
  );
}

console.log(`Exported ${tools.length} tools to ${outDir}`);

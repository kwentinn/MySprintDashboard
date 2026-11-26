// A static server for the demo copy of the page: node serve.mjs <folder> <port>. Only for make-screenshots.ps1.
import { createServer } from "node:http";
import { readFile } from "node:fs/promises";
import { extname, join, normalize } from "node:path";

const [root, port] = [process.argv[2], Number(process.argv[3])];
const types = { ".html": "text/html; charset=utf-8", ".js": "text/javascript; charset=utf-8", ".css": "text/css; charset=utf-8" };

createServer(async (request, response) => {
  const path = new URL(request.url, "http://localhost").pathname;
  const file = normalize(join(root, path === "/" ? "index.html" : path));
  if (!file.startsWith(normalize(root))) { response.writeHead(403).end(); return; }
  try {
    response.writeHead(200, { "content-type": types[extname(file)] ?? "application/octet-stream" }).end(await readFile(file));
  } catch {
    response.writeHead(404).end();
  }
}).listen(port, "127.0.0.1");

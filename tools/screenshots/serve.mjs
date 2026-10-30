// Serves the built user interface (src/ui/dist) with the desktop app's Content Security Policy:
//   node tools/screenshots/serve.mjs [dist folder]
// Listens on localhost:5173, the one browser origin the backend accepts (PORT changes it).
import { readFile } from "node:fs/promises";
import { createServer } from "node:http";
import path from "node:path";

const repoDir = path.resolve(import.meta.dirname, "../..");
const root = path.resolve(process.argv[2] ?? path.join(repoDir, "src/ui/dist"));
const config = JSON.parse(await readFile(path.join(repoDir, "src/desktop/tauri.conf.json"), "utf8"));
const csp = config.app.security.csp;
const types = {
  ".html": "text/html",
  ".js": "text/javascript",
  ".css": "text/css",
  ".woff2": "font/woff2",
  ".svg": "image/svg+xml",
  ".png": "image/png",
};

createServer(async (request, response) => {
  const route = path.normalize(new URL(request.url, "http://x").pathname).replace(/^[/\\]+/, "") || "index.html";
  const file = path.join(root, route);
  if (!file.startsWith(root)) {
    response.writeHead(403).end();
    return;
  }
  try {
    const body = await readFile(file);
    response.writeHead(200, {
      "Content-Type": types[path.extname(file)] ?? "application/octet-stream",
      "Content-Security-Policy": csp,
    });
    response.end(body);
  } catch {
    response.writeHead(404).end();
  }
}).listen(Number(process.env.PORT ?? 5173), "localhost", () =>
  console.log(`Serving ${path.relative(repoDir, root)} on http://localhost:${process.env.PORT ?? 5173}`),
);

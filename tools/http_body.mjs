// Dependency-free HTTP request body parsing shared by every ROS HTTP
// adapter (ros_server.mjs, ros_hub_server.mjs, ...). No domain logic lives
// here -- only turning a raw request into JSON or parsed multipart parts.

export function readBody(req, maxBytes) {
  return new Promise((resolve, reject) => {
    let received = 0;
    const chunks = [];
    req.on("data", (chunk) => {
      received += chunk.length;
      if (received > maxBytes) {
        reject(new Error("request body too large"));
        req.destroy();
        return;
      }
      chunks.push(chunk);
    });
    req.on("end", () => {
      const raw = Buffer.concat(chunks).toString("utf8").trim();
      if (!raw) { resolve({}); return; }
      try {
        resolve(JSON.parse(raw));
      } catch {
        reject(new Error("request body must be valid JSON"));
      }
    });
    req.on("error", reject);
  });
}

export function readRawBody(req, maxBytes) {
  return new Promise((resolve, reject) => {
    let received = 0;
    const chunks = [];
    req.on("data", (chunk) => {
      received += chunk.length;
      if (received > maxBytes) {
        reject(new Error("request body too large"));
        req.destroy();
        return;
      }
      chunks.push(chunk);
    });
    req.on("end", () => resolve(Buffer.concat(chunks)));
    req.on("error", reject);
  });
}

// Shared grammar for Content-Type and Content-Disposition headers: a bare
// token followed by `; key=value` or `; key="value"` pairs.
export function parseParamHeader(header) {
  const segments = header.split(";").map((segment) => segment.trim());
  const params = {};
  for (const segment of segments.slice(1)) {
    const eq = segment.indexOf("=");
    if (eq < 0) continue;
    let value = segment.slice(eq + 1).trim();
    if (value.startsWith('"') && value.endsWith('"')) value = value.slice(1, -1);
    params[segment.slice(0, eq).trim().toLowerCase()] = value;
  }
  return { type: segments[0], params };
}

function splitBuffer(buffer, delimiter) {
  const parts = [];
  let start = 0;
  while (true) {
    const index = buffer.indexOf(delimiter, start);
    if (index === -1) { parts.push(buffer.subarray(start)); break; }
    parts.push(buffer.subarray(start, index));
    start = index + delimiter.length;
  }
  return parts;
}

const CRLF = Buffer.from("\r\n");
const HEADER_BODY_SEPARATOR = Buffer.from("\r\n\r\n");

// Parses standard browser-generated multipart/form-data (RFC 7578's common
// subset) -- not a general-purpose RFC 7578 implementation. Built and
// verified against what fetch()+FormData actually produce.
export function parseMultipart(buffer, boundary) {
  const rawParts = splitBuffer(buffer, Buffer.from(`--${boundary}`));
  const parts = [];
  for (const raw of rawParts.slice(1, -1)) {
    const segment = raw.subarray(0, 2).equals(CRLF) ? raw.subarray(2) : raw;
    const trimmed = segment.subarray(-2).equals(CRLF) ? segment.subarray(0, -2) : segment;
    const headerEnd = trimmed.indexOf(HEADER_BODY_SEPARATOR);
    if (headerEnd === -1) continue;
    const headerText = trimmed.subarray(0, headerEnd).toString("utf8");
    const body = trimmed.subarray(headerEnd + HEADER_BODY_SEPARATOR.length);
    const headers = {};
    for (const line of headerText.split("\r\n")) {
      const colon = line.indexOf(":");
      if (colon < 0) continue;
      headers[line.slice(0, colon).trim().toLowerCase()] = line.slice(colon + 1).trim();
    }
    const disposition = headers["content-disposition"];
    if (!disposition) continue;
    const { params } = parseParamHeader(disposition);
    parts.push({ name: params.name, filename: params.filename, contentType: headers["content-type"] ?? null, body });
  }
  return parts;
}

export async function parseRequestBody(req, { maxJsonBytes, maxUploadBytes }) {
  const contentType = req.headers["content-type"] ?? "";
  if (contentType.startsWith("multipart/form-data")) {
    const { params } = parseParamHeader(contentType);
    if (!params.boundary) throw new Error("multipart request is missing a boundary");
    const raw = await readRawBody(req, maxUploadBytes);
    const parts = parseMultipart(raw, params.boundary);
    const files = parts.filter((part) => part.filename).map((part) => ({ filename: part.filename, contentType: part.contentType, data: part.body }));
    const fields = Object.fromEntries(parts.filter((part) => !part.filename && part.name).map((part) => [part.name, part.body.toString("utf8")]));
    return { files, fields };
  }
  return readBody(req, maxJsonBytes);
}

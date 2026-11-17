// Buffer HTTP responses for tunnels that do not preserve chunked transfer framing.
// Run Vite on 5174, then this loopback-only origin on 5173.
import http from 'node:http'
const hopHeaders = ['connection', 'keep-alive', 'proxy-authenticate', 'proxy-authorization', 'te', 'trailer', 'transfer-encoding', 'upgrade']
http.createServer((request, response) => {
  const headers = { ...request.headers }
  for (const key of hopHeaders) delete headers[key]
  const upstream = http.request({ hostname: '127.0.0.1', port: 5174, method: request.method, path: request.url, headers }, result => {
    const chunks = []
    result.on('data', chunk => chunks.push(chunk))
    result.on('end', () => {
      const body = Buffer.concat(chunks)
      const replyHeaders = { ...result.headers }
      for (const key of hopHeaders) delete replyHeaders[key]
      if (![204, 304].includes(result.statusCode) && request.method !== 'HEAD') replyHeaders['content-length'] = String(body.length)
      response.writeHead(result.statusCode, replyHeaders)
      response.end(body)
    })
    result.on('error', () => response.destroy())
  })
  upstream.on('error', () => {
    if (!response.headersSent) response.writeHead(502, { 'content-type': 'application/json' })
    response.end(JSON.stringify({ detail: 'Không kết nối được máy chủ ứng dụng. Hãy thử lại.' }))
  })
  request.on('aborted', () => upstream.destroy())
  request.pipe(upstream)
}).listen(5173, '127.0.0.1', () => console.log('Tunnel origin: 127.0.0.1:5173 → Vite :5174'))

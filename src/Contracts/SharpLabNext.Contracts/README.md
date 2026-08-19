# SharpLabNext Contracts

This package contains the stable, transport-neutral request, result, operation,
workspace, diagnostic, selection, and worker identity contracts used by
SharpLabNext extension points. It has no dependency on Gateway, a compiler, a
runtime, Docker, or an artifact store implementation.

Extension packages should use the contract types directly and negotiate the
published protocol version. Do not duplicate these records or infer a protocol
from HTTP route implementation details.

## Wire naming

Business HTTP endpoints, business WebSockets, and SharpLabNext-owned internal
JSON records use one canonical member shape: PascalCase. This includes
anonymous response objects and framework-shaped errors when they are emitted
by a SharpLabNext endpoint. Reads are strict; the old lower-camel spelling is
not accepted. Enum values retain their documented kebab-case values, and the
polymorphic `Kind`/`ResultType` discriminators use PascalCase property names.
An unknown but correctly named future discriminator is retained as an opaque
base value for forward compatibility; its payload is not interpreted by the
current service. Case-only aliases of the reserved discriminator names are
still rejected.

SharpLabNext-owned URL query keys and multipart field names follow the same
strict spelling (`FromSequence`, `ReturnPath`, `Target`, `Branch`, `Mode`,
`TtlSeconds`, `Manifest`, and `Files`). Standard protocol fields belonging to
LSP/JSON-RPC, Docker, GitHub, SSE, or HTTP itself are not rewritten.

Use `ApplySerializerOptions` when configuring a host-owned ASP.NET JSON
options instance. Versioned catalog/profile/storage files keep their existing
canonical Web shape through `CreateCanonicalSerializerOptions`; they are not
runtime interaction envelopes. LSP/JSON-RPC, Docker, GitHub, and other
external formats must use their dedicated options because those names are
defined outside SharpLabNext. SharpLabNext-owned runtime/service envelopes use
the PascalCase business options.

Language-server WebSockets are a separate LSP/JSON-RPC protocol and must use
`ContractJson.CreateLspSerializerOptions`; they remain lower camel-case as
required by the LSP specification.
